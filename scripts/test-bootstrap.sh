#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
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
  'auth status') exit 0 ;;
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
cat > "$test_root/failing-sha256sum/sha256sum" <<'EOF'
#!/bin/sh
exit 1
EOF
chmod +x "$test_root/failing-sha256sum/sha256sum"

cat > "$test_root/payload/orelay" <<'EOF'
#!/bin/sh
printf '%s\n' "$@" > "$ARG_LOG"
exit "${FIXTURE_EXIT:-0}"
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
  PATH="$script_path" FIXTURE_ROOT="$test_root/fixture" FIXTURE_ARCHIVE=orelay-1.2.3-linux-x64.tar.gz HOME="$test_root/home" TMPDIR="$test_root/tmp" ARG_LOG="$test_root/args" \
    "$@" sh "$root/install.sh" --version 1.2.3 --install-dir "$test_root/install path" \
    --config-file "$test_root/state file.json" --name 'relay service' --restart-service
}

run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64
grep -Fxq 'install' "$test_root/args"
grep -Fxq "$test_root/install path" "$test_root/args"
grep -Fxq "$test_root/state file.json" "$test_root/args"
grep -Fxq 'relay service' "$test_root/args"
grep -Fxq -- '--restart-service' "$test_root/args"

for pair in 'Linux x86_64 linux-x64' 'Linux aarch64 linux-arm64' 'Darwin x86_64 osx-x64' 'Darwin arm64 osx-arm64'; do
  set -- $pair
  run_install env FIXTURE_OS="$1" FIXTURE_ARCH="$2" FIXTURE_ARCHIVE="orelay-1.2.3-$3.tar.gz"
  grep -Fxq "$test_root/install path" "$test_root/args"
done
if ! run_install env FIXTURE_OS=Linux FIXTURE_ARCH=x86_64 FIXTURE_FORCE_SHASUM=1; then
  printf 'The shasum checksum fallback failed.\n' >&2; exit 1
fi

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
[ -z "$(find "$test_root/tmp" -mindepth 1 -maxdepth 1 -name 'orelay-bootstrap.*' -print -quit)" ] || {
  printf 'Bootstrap left a temporary download directory behind.\n' >&2; exit 1;
}
printf '%s\n' 'bootstrap fixture checks passed'
