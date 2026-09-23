#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
if command -v setsid >/dev/null 2>&1; then no_tty=setsid; else no_tty=; fi
test_root=$(mktemp -d "${TMPDIR:-/tmp}/orelay-bootstrap-test.XXXXXXXX")
cleanup() { rm -rf -- "$test_root"; }
trap cleanup EXIT HUP INT TERM
mkdir -p "$test_root/bin" "$test_root/failing-sha256sum" "$test_root/fixture" "$test_root/payload" "$test_root/home" "$test_root/tmp"

cat > "$test_root/bin/uname" <<'EOF'
#!/bin/sh
case "$1" in -s) printf '%s\n' "${FIXTURE_OS:-Linux}" ;; -m) printf '%s\n' "${FIXTURE_ARCH:-x86_64}" ;; esac
EOF
cat > "$test_root/bin/gh" <<'EOF'
#!/bin/sh
set -eu
case "$1 $2" in
  'auth status') [ "${FIXTURE_NO_GH:-}" != 1 ]; exit $? ;;
  'release view')
    case "${3:-}" in
      --repo) printf '%s\n' 'v1.2.3'; exit 0 ;;
      v*)
        case "${FIXTURE_MISSING:-}" in archive) exit 0 ;; checksum) printf '%s\n' "$FIXTURE_ARCHIVE"; exit 0 ;; esac
        printf '%s\n' "$FIXTURE_ARCHIVE" "$FIXTURE_ARCHIVE.sha256"
        exit 0 ;;
    esac ;;
  'release download')
    shift 2
    tag=$1; shift
    dest=''
    patterns=''
    while [ "$#" -gt 0 ]; do
      case "$1" in --pattern) patterns="$patterns $2"; shift 2 ;; --dir) dest=$2; shift 2 ;; *) shift ;; esac
    done
    for name in $patterns; do
      if [ "${FIXTURE_MISSING:-}" != "$name" ]; then cp "$FIXTURE_ROOT/$name" "$dest/$name"; fi
    done
    exit 0 ;;
esac
exit 2
EOF
chmod +x "$test_root/bin/uname" "$test_root/bin/gh"
cat > "$test_root/bin/curl" <<'EOF'
#!/bin/sh
set -eu
destination=''
url=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) destination=$2; shift 2 ;;
    --connect-timeout|--max-time|--write-out|--proto) shift 2 ;;
    --header|-H|--config) printf 'Anonymous download sent authentication options.\n' >&2; exit 1 ;;
    https://*) url=$1; shift ;;
    *) shift ;;
  esac
done
case "$url" in
  https://github.com/demyte/ORelay/releases/latest) printf '%s' 'https://github.com/demyte/ORelay/releases/tag/v1.2.3' ;;
  https://github.com/demyte/ORelay/releases/download/v1.2.3/*) cp "$FIXTURE_ROOT/${url##*/}" "$destination" ;;
  *) printf 'Unexpected public download URL.\n' >&2; exit 1 ;;
esac
EOF
chmod +x "$test_root/bin/curl"
cat > "$test_root/failing-sha256sum/sha256sum" <<'EOF'
#!/bin/sh
exit 1
EOF
chmod +x "$test_root/failing-sha256sum/sha256sum"

cat > "$test_root/payload/orelay" <<'EOF'
#!/bin/sh
case "$1" in
  install)
    printf '%s\n' "$@" > "$ARG_LOG"
    [ "${FIXTURE_EXIT:-0}" -eq 0 ] || exit "$FIXTURE_EXIT"
    shift
    [ "$1" = --install-dir ]; shift
    dest=$1
    mkdir -p "$dest"
    cp "$0" "$dest/orelay"
    ;;
  setup)
    printf '%s\n' "$0" "$@" > "$SETUP_LOG"
    exit "${FIXTURE_SETUP_EXIT:-0}"
    ;;
  *) exit 88 ;;
esac
EOF
chmod +x "$test_root/payload/orelay"
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  archive="$test_root/fixture/orelay-1.2.3-$rid.tar.gz"
  tar -czf "$archive" -C "$test_root/payload" .
  if command -v sha256sum >/dev/null 2>&1; then hash=$(sha256sum "$archive" | awk '{print $1}'); else hash=$(shasum -a 256 "$archive" | awk '{print $1}'); fi
  printf '%s  %s\n' "$hash" "$(basename "$archive")" > "$archive.sha256"
done

run_install() {
  if [ "${FIXTURE_FORCE_SHASUM:-}" = 1 ]; then script_path="$test_root/failing-sha256sum:$test_root/bin:$PATH"; else script_path="$test_root/bin:$PATH"; fi
  PATH="$script_path" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" \
    $no_tty "$@" sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" \
    --config-file "$test_root/state file.json" --name 'relay service' --restart-service
}

run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64
grep -Fxq 'install' "$test_root/args"
grep -Fxq "$test_root/install path" "$test_root/args"
grep -Fxq "$test_root/state file.json" "$test_root/args"
grep -Fxq 'relay service' "$test_root/args"
grep -Fxq -- '--restart-service' "$test_root/args"
[ ! -e "$test_root/setup-args" ] || { printf 'Unattended install started the wizard.\n' >&2; exit 1; }
printf '%s\n' 'script source must never become wizard input' | \
  run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 > "$test_root/piped-output"
[ ! -e "$test_root/setup-args" ] || { printf 'Piped install started the wizard without a terminal.\n' >&2; exit 1; }

PATH="$test_root/bin:$PATH" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" \
  ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 \
  $no_tty sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" \
  --config-file "$test_root/state file.json" --name 'relay service' --defaults --add-to-path
[ "$(sed -n '1p' "$test_root/setup-args")" = "$test_root/install path/orelay" ] || {
  printf 'Setup did not run from the installed executable.\n' >&2; exit 1;
}
for arg in setup --if-needed --defaults --yes --add-to-path "$test_root/state file.json" 'relay service'; do
  grep -Fxq -- "$arg" "$test_root/setup-args" || { printf 'Missing setup argument: %s\n' "$arg" >&2; exit 1; }
done
PATH="$test_root/bin:$PATH" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" \
  ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 \
  $no_tty sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" --defaults --skip-path
grep -Fxq -- '--skip-path' "$test_root/setup-args"
if grep -Fxq -- '--add-to-path' "$test_root/setup-args"; then
  printf 'Skip PATH setup also forwarded add-to-path.\n' >&2; exit 1
fi
if PATH="$test_root/bin:$PATH" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" \
  ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 FIXTURE_SETUP_EXIT=23 \
  $no_tty sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" --defaults >/dev/null 2>&1; then
  printf 'Setup failure was not passed through.\n' >&2; exit 1
else
  status=$?
  [ "$status" -eq 23 ] || { printf 'Expected setup exit code 23, got %s.\n' "$status" >&2; exit 1; }
fi
rm -f "$test_root/setup-args"
PATH="$test_root/bin:$PATH" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" \
  ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 \
  $no_tty sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" \
  --config-file "$test_root/state file.json" --name 'relay service' --skip-setup > "$test_root/skip-output"
[ ! -e "$test_root/setup-args" ] || { printf 'Skip setup started the wizard.\n' >&2; exit 1; }
grep -Fxq "Installed executable: $test_root/install path/orelay" "$test_root/skip-output"
grep -Fxq 'Run it with: setup --if-needed' "$test_root/skip-output"
grep -Fxq -- "  --config-file: $test_root/state file.json" "$test_root/skip-output"
grep -Fxq -- '  --name: relay service' "$test_root/skip-output"
if sh "$root/install.sh" --defaults --skip-setup > /dev/null 2>&1; then
  printf 'Conflicting setup flags were accepted.\n' >&2; exit 1
fi
for flags in '--add-to-path --skip-path' '--add-to-path --skip-setup' '--skip-path --skip-setup' '--add-to-path'; do
  rm -f "$test_root/args"
  if PATH="$test_root/bin:$PATH" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" \
    ARG_LOG="$test_root/args" SETUP_LOG="$test_root/setup-args" FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 \
    $no_tty sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" $flags > /dev/null 2>&1; then
    printf 'Invalid PATH flags succeeded: %s\n' "$flags" >&2; exit 1
  fi
  [ ! -e "$test_root/args" ] || { printf 'Invalid PATH flags reached installation: %s\n' "$flags" >&2; exit 1; }
done

for pair in 'Linux x86_64 linux-x64' 'Linux aarch64 linux-arm64' 'Darwin x86_64 osx-x64' 'Darwin arm64 osx-arm64'; do
  set -- $pair
  run_install env FIXTURE_OS="$1" FIXTURE_ARCH="$2" FIXTURE_ARCHIVE="orelay-1.2.3-$3.tar.gz"
  grep -Fxq "$test_root/install path" "$test_root/args"
done
if ! run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 FIXTURE_FORCE_SHASUM=1; then
  printf 'The shasum checksum fallback failed.\n' >&2; exit 1
fi

# Public installs do not need gh, even if an unrelated token is in the environment.
PATH="$test_root/bin:$PATH" FIXTURE_NO_GH=1 FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 \
  FIXTURE_ROOT="$test_root/fixture" HOME="$test_root/home" TMPDIR="$test_root/tmp" \
  GH_TOKEN=unused-fixture-token ARG_LOG="$test_root/public-args" SETUP_LOG="$test_root/public-setup-args" \
  $no_tty sh "$root/install.sh" --install-dir "$test_root/public install"
grep -Fxq "$test_root/public install" "$test_root/public-args"

if run_install env FIXTURE_OS=Darwin FIXTURE_ARCH=arm64 >/dev/null 2>&1; then
  printf 'Unsupported platform unexpectedly ran the installer.\n' >&2; exit 1
fi
if run_install env FIXTURE_OS=Linux FIXTURE_ARCH=aarch64 >/dev/null 2>&1; then
  printf 'Missing platform asset unexpectedly ran the installer.\n' >&2; exit 1
fi
if run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz FIXTURE_MISSING=archive >/dev/null 2>&1; then
  printf 'Missing archive unexpectedly ran the installer.\n' >&2; exit 1
fi
if run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 FIXTURE_EXIT=19 >/dev/null 2>&1; then
  printf 'Installer exit code was not passed through.\n' >&2; exit 1
else
  status=$?
  [ "$status" -eq 19 ] || { printf 'Expected installer exit code 19, got %s.\n' "$status" >&2; exit 1; }
fi

printf '%s\n' 'not-a-valid-checksum  orelay-1.2.3-linux-x64.tar.gz' > "$test_root/fixture/orelay-1.2.3-linux-x64.tar.gz.sha256"
if run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 >/dev/null 2>&1; then
  printf 'Checksum failure unexpectedly ran the installer.\n' >&2; exit 1
fi
set -- "$test_root/tmp"/orelay-bootstrap.*
[ "$1" = "$test_root/tmp/orelay-bootstrap.*" ] || {
  printf 'Bootstrap left a temporary download directory behind.\n' >&2; exit 1;
}
printf '%s\n' 'bootstrap fixture checks passed'
