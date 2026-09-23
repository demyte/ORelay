# CLI reference

Use `orelay <command> --help` for syntax. Global options can appear before or after the command.

| Option | Meaning |
| --- | --- |
| `--config-file <path>` | Select a config file. Relative paths resolve from the invoking directory. The default is `orelay.json` beside the executable. |
| `--json` | Return structured command results. Argument parsing errors and help remain text. |
| `--help`, `-h` | Print help without creating configuration. |
| `--version`, `-v` | Print the executable version without creating configuration. |

Version output includes the SemVer version and source commit, for example `0.2.0-rc.1+<commit>`. The same stamp is embedded in the executable. See [versions and releases](releases.md) for tag and development-build rules.

## Configuration

```text
orelay init --port 12987
orelay config get
orelay config get port --json
orelay config set port 13000
orelay config clear port
```

`init` creates a missing file from defaults and supplied settings. If the file exists, it validates and preserves it. `config get` is read-only and shows defaults when the selected file is missing. `config set` changes one saved setting. `config clear` removes that saved override so the built-in default applies. Unknown keys and malformed files fail without overwriting the file.

The JSON file has `schemaVersion: 1`. Updates use a sibling lock and atomic replacement, so simultaneous CLI updates preserve unrelated settings. The sibling `.lock` file can remain after use; the operating-system lock is held only during an update.

| Config key | Flag for `init` and `server` | Default |
| --- | --- | --- |
| `port` | `--port` | `12987` |
| `bind` | `--bind` | `127.0.0.1` |
| `publicUrl` | `--public-url` | Unset |
| `hostname` | `--hostname` | `localhost` |
| `autoDiscovery` | `--auto-discovery` | `none` |
| `leaseSeconds` | `--lease-seconds` | `300` |
| `maxRegistrations` | `--max-registrations` | `1000` |

Ports range from 1 to 65535, leases from 1 to 86400 seconds, and registration capacity from 1 to 1000000. Discovery accepts `none`, `local`, or `tailscale`. `none` and `local` use the configured listener/hostname for the relay; neither runs Tailscale.

For `tailscale`, an unset hostname is discovered instead of using the `localhost` default. If an existing file contains `hostname`, it remains an explicit override. Use `orelay config clear hostname` to allow discovery after selecting `tailscale`.

`publicUrl` takes precedence over `hostname`. It can be a base URL, with `/callback` appended, or a complete URL ending in `/callback`. A path prefix is preserved. It must use HTTP or HTTPS without credentials, a query, or a fragment. ORelay itself serves HTTP; an HTTPS public URL requires an operator-managed TLS endpoint that forwards to it.

## Foreground server

```text
orelay server
orelay server --port 13000
orelay --config-file ./orelay.shared.json server --bind 0.0.0.0 --hostname relay.example.test
```

Effective settings use built-in defaults, then saved values, then command-line flags. On the first start, a missing file is created with the supplied flags. When the file exists, server flags apply only to that process and are not saved. Use `config set` for permanent changes. Config changes take effect on the next start.

A wildcard bind needs a usable advertised address. A bind address controls listening; an advertised hostname or public URL controls what the provider and browser use. Setting a hostname does not make a loopback listener remotely reachable.

Stop the foreground process with Ctrl+C. The server persists registration IDs, destinations, and UTC lease expiry in a SQLite file beside the selected configuration. For `orelay.json`, the database is `orelay.registrations.db`. Use separate configuration paths for independent relays. Keep this directory writable by the service account and retain the database during upgrades. SQLite is compiled into the native executable.

Registrations and renewals commit before the relay acknowledges them. Deletions and expiry cleanup are durable. Live registrations survive a restart with their original expiry; the clock continues while the server is stopped. Unknown, deleted, or expired registrations still fail. A corrupt or incompatible database prevents startup rather than starting with an empty registry. The relay never persists codes or tokens, and normal server logs omit callback query values.

Server logs go to stderr. Each line has a local timestamp and a level, coloured in an interactive terminal. Registration and callback messages identify a worktree by the first eight characters of its registration ID and its destination origin, such as `http://localhost:5017`. Destination paths and callback query values are omitted. Health probes and successful lease renewals stay quiet.

Redirecting stderr produces plain text without colour codes. Set `NO_COLOR=1` or `TERM=dumb` to disable colour in a terminal. With `server --json`, stdout contains the startup readiness object and stderr contains one JSON object per log event. Colour is disabled in JSON mode.

## Doctor

```text
orelay doctor --json
orelay --config-file ./orelay.json doctor --fix
```

Doctor checks the selected config, listener and advertised address, relay health identity, port occupancy, discovery prerequisites, and supported service state. It is read-only by default. `--fix` creates a missing config file, then runs the same checks. It does not replace malformed files, stop port owners, modify network bindings, or install services. A successful config repair can still return an unhealthy result if the relay is not running.

When `localhost` is advertised on an explicit loopback IP binding, the health check uses that bound IP to avoid probing the wrong address family. An explicit `publicUrl` keeps its configured health-check address.

## Services

Windows Service and Linux systemd commands use the same published native executable:

```text
orelay --config-file <absolute-config-path> service install
orelay --config-file <absolute-config-path> service start
orelay --config-file <absolute-config-path> service status --json
orelay --config-file <absolute-config-path> service restart
orelay --config-file <absolute-config-path> service stop
orelay --config-file <absolute-config-path> service uninstall
```

Install from a stable executable location with the selected config already initialized. Service management requires the platform's administrative privileges. Windows uses the service control manager; Linux uses a system-level systemd unit. macOS supports foreground execution, with no service installer in this version.

The installed definition records absolute executable and config paths. Use the same paths for later management commands. Conflicting definitions are reported instead of overwritten. Uninstall preserves the config, registration database, and executable. A service restart preserves unexpired registrations just like a foreground restart.

Use `--name <name>` on service commands and doctor to select another service identity. Windows defaults to `ORelay`, running as LocalSystem with demand start. Linux defaults to `orelay.service`, running as root. Install creates the definition but does not start it or enable boot-time startup. Linux operators can separately run `sudo systemctl enable orelay.service` when they want boot-time startup. Grant the service account access to the chosen configuration directory.

## Installation and updates

```text
orelay install --install-dir <path>
orelay update --check --json
orelay update
orelay --config-file <absolute-config-path> update --restart-service --name <service-name>
```

`install` copies the running native executable to a stable directory. The default is `%LOCALAPPDATA%\ORelay` on Windows and `~/.local/bin` on Unix. It preserves existing configuration and registration data. Add the directory to your `PATH`; the command does not edit shell profiles or install a service. The repository's `install.ps1` and `install.sh` bootstraps download a verified release and invoke this command.

`update --check` reads the latest stable GitHub release. `update` verifies the matching archive checksum and executable version before replacing the installed binary. Development builds are never implicitly downgraded to an older stable release. Managed `dotnet run` builds do not support installation or updates.

`--restart-service` explicitly permits restarting the selected service. Supply its original `--config-file` and custom `--name` when applicable. An initially stopped service stays stopped. A running service must pass startup checks after replacement; failures attempt to restore the previous executable and service state. Configuration and database files are not rolled back. Database schema changes in future releases must therefore preserve executable rollback compatibility.

Private release access uses `GH_TOKEN`, `GITHUB_TOKEN`, or the current GitHub CLI login. Public releases can be downloaded without credentials. Credentials are never saved in relay configuration. Both commands accept `--json`, keep diagnostics on stderr, and use the exit codes below. Updates are explicit; automatic background installation is not implemented.

An upgrade from the original in-memory releases cannot recover their live registrations. After that first upgrade, applications must explicitly restart to register in the persistent store.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Command succeeded, or doctor found no failures. |
| `1` | Doctor found a problem. |
| `3` | Configuration or command execution failed. |
| `64` | Invalid command, arguments, or config key. |
| `69` | Command unavailable in this environment or build. |

Service-specific failures and their structured `errorCode` are described in command help. Check the exit status before consuming stdout. JSON output contains the result; stderr contains human-readable diagnostics.
