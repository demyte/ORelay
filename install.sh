#!/bin/sh
set -eu

repo='demyte/ORelay'
version=''
install_dir=''
config_file=''
service_name=''
restart_service=0
setup_defaults=0
skip_setup=0
add_to_path=0
skip_path=0

usage() {
    printf '%s\n' 'Usage: install.sh [--version X.Y.Z] [--install-dir PATH] [--config-file PATH] [--restart-service] [--name SERVICE] [--defaults | --skip-setup] [--add-to-path | --skip-path]'
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            version=$2; shift 2 ;;
        --install-dir)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            install_dir=$2; shift 2 ;;
        --config-file)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            config_file=$2; shift 2 ;;
        --name)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            service_name=$2; shift 2 ;;
        --restart-service) restart_service=1; shift ;;
        --defaults) setup_defaults=1; shift ;;
        --skip-setup) skip_setup=1; shift ;;
        --add-to-path) add_to_path=1; shift ;;
        --skip-path) skip_path=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done
[ "$setup_defaults" -eq 0 ] || [ "$skip_setup" -eq 0 ] || {
    printf '%s\n' '--defaults and --skip-setup cannot be used together.' >&2; exit 2;
}
[ "$add_to_path" -eq 0 ] || [ "$skip_path" -eq 0 ] || {
    printf '%s\n' '--add-to-path and --skip-path cannot be used together.' >&2; exit 2;
}
[ "$skip_setup" -eq 0 ] || { [ "$add_to_path" -eq 0 ] && [ "$skip_path" -eq 0 ]; } || {
    printf '%s\n' 'PATH options require setup; remove --skip-setup.' >&2; exit 2;
}
interactive_terminal=0
if [ -t 1 ] && ( : </dev/tty ) 2>/dev/null && ( test -t 3 ) 3</dev/tty 2>/dev/null; then
    interactive_terminal=1
fi
if [ "$add_to_path" -eq 1 ] && [ "$setup_defaults" -eq 0 ] && [ "$interactive_terminal" -eq 0 ]; then
    printf '%s\n' '--add-to-path requires a terminal or --defaults for unattended setup.' >&2; exit 2
fi

case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *) printf 'Unsupported operating system.\n' >&2; exit 2 ;;
esac
case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) printf 'Unsupported processor architecture.\n' >&2; exit 2 ;;
esac
rid="$os-$arch"

gh_auth=0
if command -v gh >/dev/null 2>&1 && gh auth status -h github.com >/dev/null 2>&1; then
    gh_auth=1
fi

tmp=$(mktemp -d "${TMPDIR:-/tmp}/orelay-bootstrap.XXXXXXXX") || exit 1
cleanup() { rm -rf -- "$tmp"; }
trap cleanup EXIT HUP INT TERM

if [ -z "$version" ]; then
    if [ "$gh_auth" -eq 1 ]; then
        tag=$(gh release view --repo "$repo" --json tagName --jq .tagName 2>/dev/null) || {
            printf 'Could not read the latest ORelay release.\n' >&2; exit 1;
        }
    else
        command -v curl >/dev/null 2>&1 || { printf 'Install curl or authenticate gh to download ORelay.\n' >&2; exit 1; }
        effective=$(curl --connect-timeout 15 --max-time 120 --fail --silent --show-error --location --output /dev/null --write-out '%{url_effective}' 'https://github.com/demyte/ORelay/releases/latest') || {
            printf 'Could not find the latest stable ORelay release.\n' >&2; exit 1;
        }
        tag=${effective##*/}
    fi
    case "$tag" in v*) version=${tag#v} ;; *) printf 'Release tag is invalid.\n' >&2; exit 1 ;; esac
fi
case "$version" in
    ''|*[!0-9.]*|.*|*..*|*.) printf 'Version must be a stable X.Y.Z release.\n' >&2; exit 2 ;;
esac
old_ifs=$IFS; IFS=.; set -- $version; IFS=$old_ifs
[ "$#" -eq 3 ] || { printf 'Version must be a stable X.Y.Z release.\n' >&2; exit 2; }
for part do case "$part" in ''|*[!0-9]*) printf 'Version must be a stable X.Y.Z release.\n' >&2; exit 2 ;; esac; done
if [ "$1" -eq 0 ] && [ "$2" -lt 2 ]; then
    printf 'ORelay v%s predates the installer. Choose version 0.2.0 or later, or extract that release manually.\n' "$version" >&2
    exit 2
fi
tag="v$version"
archive="orelay-$version-$rid.tar.gz"
checksum="$archive.sha256"

if [ -z "$install_dir" ]; then
    [ -n "${HOME:-}" ] || { printf 'HOME is not set.\n' >&2; exit 2; }
    install_dir="$HOME/.local/bin"
fi
if [ "$gh_auth" -eq 1 ]; then
    if ! gh release view "$tag" --repo "$repo" --json assets --jq '.assets[].name' 2>/dev/null | grep -Fqx "$archive"; then
        printf 'Release %s does not contain %s.\n' "$tag" "$archive" >&2; exit 1
    fi
    if ! gh release view "$tag" --repo "$repo" --json assets --jq '.assets[].name' 2>/dev/null | grep -Fqx "$checksum"; then
        printf 'Release %s does not contain %s.\n' "$tag" "$checksum" >&2; exit 1
    fi
    gh release download "$tag" --repo "$repo" --pattern "$archive" --pattern "$checksum" --dir "$tmp" >/dev/null || {
        printf 'Could not download the ORelay release assets.\n' >&2; exit 1;
    }
else
    base="https://github.com/$repo/releases/download/$tag"
    curl --connect-timeout 15 --max-time 120 --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$base/$archive" --output "$tmp/$archive" || {
        printf 'Release asset %s is unavailable.\n' "$archive" >&2; exit 1;
    }
    curl --connect-timeout 15 --max-time 120 --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$base/$checksum" --output "$tmp/$checksum" || {
        printf 'Release checksum %s is unavailable.\n' "$checksum" >&2; exit 1;
    }
fi

[ -f "$tmp/$archive" ] && [ -f "$tmp/$checksum" ] || { printf 'The release is missing an expected asset.\n' >&2; exit 1; }
expected=$(awk -v name="$archive" '$2 == name { if (length($1) == 64 && $1 ~ /^[[:xdigit:]]+$/ && NF == 2) { print tolower($1); found++ } } END { if (found != 1) exit 1 }' "$tmp/$checksum") || {
    printf 'The release checksum has an invalid name or format.\n' >&2; exit 1;
}
if command -v sha256sum >/dev/null 2>&1 && actual=$(sha256sum "$tmp/$archive" 2>/dev/null | awk '{print tolower($1)}') && [ -n "$actual" ]; then
    :
elif command -v shasum >/dev/null 2>&1 && actual=$(shasum -a 256 "$tmp/$archive" 2>/dev/null | awk '{print tolower($1)}') && [ -n "$actual" ]; then
    :
else
    printf 'Install sha256sum or shasum to verify the release.\n' >&2; exit 1
fi
[ "$expected" = "$actual" ] || { printf 'The release checksum does not match.\n' >&2; exit 1; }

tar -tzf "$tmp/$archive" > "$tmp/archive-list" || { printf 'The release archive is invalid.\n' >&2; exit 1; }
if awk 'BEGIN { bad=0 } /^\// || /(^|\/)\.\.(\/|$)/ { bad=1 } END { exit bad }' "$tmp/archive-list"; then :; else
    printf 'The release archive contains an unsafe path.\n' >&2; exit 1
fi
awk '$0 == "./orelay" { found++ } END { if (found != 1) exit 1 }' "$tmp/archive-list" || {
    printf 'The release archive must contain exactly one root orelay executable.\n' >&2; exit 1;
}
duplicates=$(sort "$tmp/archive-list" | uniq -d)
[ -z "$duplicates" ] || { printf 'The release archive contains duplicate paths.\n' >&2; exit 1; }
tar -tvzf "$tmp/$archive" > "$tmp/archive-details" || { printf 'The release archive is invalid.\n' >&2; exit 1; }
awk 'substr($0, 1, 1) != "-" && substr($0, 1, 1) != "d" { bad=1 } END { exit bad }' "$tmp/archive-details" || {
    printf 'The release archive contains a link or special file.\n' >&2; exit 1;
}
mkdir "$tmp/unpacked"
tar -xzf "$tmp/$archive" -C "$tmp/unpacked" ./orelay || { printf 'Could not extract the ORelay executable.\n' >&2; exit 1; }
exe="$tmp/unpacked/orelay"
[ -f "$exe" ] && [ ! -L "$exe" ] || { printf 'The release archive does not contain a regular ORelay executable.\n' >&2; exit 1; }
chmod u+x "$exe"

set -- install --install-dir "$install_dir"
[ -z "$config_file" ] || set -- "$@" --config-file "$config_file"
[ -z "$service_name" ] || set -- "$@" --name "$service_name"
[ "$restart_service" -eq 0 ] || set -- "$@" --restart-service
"$exe" "$@"
install_status=$?
[ "$install_status" -eq 0 ] || exit "$install_status"

installed="$install_dir/orelay"
set -- setup --if-needed
[ -z "$config_file" ] || set -- "$@" --config-file "$config_file"
[ -z "$service_name" ] || set -- "$@" --name "$service_name"
[ "$add_to_path" -eq 0 ] || set -- "$@" --add-to-path
[ "$skip_path" -eq 0 ] || set -- "$@" --skip-path
print_setup_next_step() {
    printf 'Installed executable: %s\n' "$installed"
    printf '%s\n' 'Run it with: setup --if-needed'
    [ -z "$config_file" ] || printf '  --config-file: %s\n' "$config_file"
    [ -z "$service_name" ] || printf '  --name: %s\n' "$service_name"
    [ "$skip_path" -eq 0 ] || printf '%s\n' '  --skip-path'
}
if [ "$skip_setup" -eq 1 ]; then
    print_setup_next_step
elif [ "$setup_defaults" -eq 1 ]; then
    "$installed" "$@" --defaults --yes
else
    # A piped installer owns standard input. The wizard must use the terminal directly.
    if [ "$interactive_terminal" -eq 1 ]; then
        "$installed" "$@" </dev/tty
    else
        print_setup_next_step
    fi
fi
