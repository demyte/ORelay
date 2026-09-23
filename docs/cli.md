# CLI reference

Use `orelay <command> --help` for syntax. Global options can appear before or after the command.

| Option | Meaning |
| --- | --- |
| `--config-file <path>` | Select a config file. Relative paths resolve from the invoking directory. The default is `orelay.json` beside the executable. |
| `--json` | Return structured command results. Argument parsing errors and help remain text. |
| `--help`, `-h` | Print help without creating configuration. |
| `--version`, `-v` | Print the executable version without creating configuration. |

Version output includes the SemVer version and source commit, for example `0.2.0-rc.1+<commit>`. The same stamp is embedded in the executable. See [versions and releases](releases.md) for tag and development-build rules.

## Setup

Run `orelay setup` for guided configuration. The first screen shows the defaults and offers **Go with defaults** or **Customize**. Defaults mean local access at `127.0.0.1:12987`, callback URL `http://localhost:12987/callback`, no discovery, a five-minute lease, capacity for 1,000 registrations, and foreground operation without a service or boot startup. Review the summary and confirm to save. Setup prints the foreground command rather than keeping the installer terminal occupied.

Custom setup asks about local, LAN, or Tailscale access, port, bind address, advertised hostname, and foreground or service operation. Windows and Linux service setup also asks for the service name, whether to start it now, and whether to start it at boot. Starting an already running service restarts it to load the saved settings. macOS supports foreground operation only.

Tailscale must already be installed and connected. Setup checks discovery before saving. The Tailscale preset binds all IPv4 interfaces and discovers the advertised hostname; it does not restrict access to the tailnet or configure firewall rules. Choose a specific interface address when needed. LAN access defaults to the machine hostname. Confirm that the browser and consuming apps can reach the advertised address. Shared management has no authentication in this version.

Setup offers to add the executable's directory to your user PATH so a new terminal can run `orelay` from any folder. This also works with a custom installation directory. Review the destination and confirm before the change is applied. `--skip-path` disables this offer. Unattended `--yes` leaves PATH unchanged unless you also pass `--add-to-path`.

On Windows, setup updates the user PATH. For sh, bash, zsh, and fish, it appends a quoted PATH entry to the user's shell startup files and preserves their existing content. Bash updates both its interactive and login profiles; zsh respects `ZDOTDIR`, and fish respects `XDG_CONFIG_HOME`. Setup prints the files it will change. Other shells require manual PATH configuration. Changes apply to the account running setup, including when run as root or another administrator.

Existing configurations are offered as the starting point. Interactive cancellation or ended input applies no changes. Setup saves the reviewed settings in one atomic write and rejects concurrent configuration changes. `--if-needed` and `--defaults` preserve a valid existing configuration while still allowing PATH setup. Use plain `setup` to revisit relay settings.

For unattended operation, use `--yes`. It accepts the displayed choices without prompting and can be combined with `--json`:

```text
orelay setup --defaults --yes
orelay setup --defaults --yes --add-to-path
orelay --config-file /data/orelay.json setup --yes --access lan --port 13000 --bind 192.168.1.20 --hostname relay.example.test
orelay --config-file <absolute-config-path> setup --yes --access tailscale --mode service --name orelay-dev --enable-startup --start
```

Service setup requires administrative privileges and a stable published executable. Setup checks the selected service's ownership before saving, but later service-manager or PATH actions can fail. In that case, the saved configuration and any completed actions remain applied. Correct the reported problem and rerun with the same name and configuration path. Service mode without `--enable-startup` selects manual startup. Foreground mode leaves existing service installations in place.

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

| Config key | Flag for `init`, `setup`, and `server` | Default |
| --- | --- | --- |
| `port` | `--port` | `12987` |
| `bind` | `--bind` | `127.0.0.1` |
| `publicUrl` | `--public-url` | Unset |
| `hostname` | `--hostname` | `localhost` |
| `autoDiscovery` | `--auto-discovery` | `none` |
| `leaseSeconds` | `--lease-seconds` | `300` |
| `maxRegistrations` | `--max-registrations` | `1000` |
| `autoUpdate` | Config only | `true` |
| `autoUpdateIntervalSeconds` | Config only | `10800` |

Ports range from 1 to 65535, leases from 1 to 86400 seconds, and registration capacity from 1 to 1000000. Discovery accepts `none`, `local`, or `tailscale`. `none` and `local` use the configured listener/hostname for the relay; neither runs Tailscale.

`autoUpdate` accepts `true` or `false`. `autoUpdateIntervalSeconds` accepts 60 to 2592000 seconds. These saved settings apply only to a published executable running under Windows Service Control Manager or Linux systemd. Updates are enabled by default, with checks every three hours. Existing saved `false` values and custom intervals stay in effect until changed or cleared. Changing either setting takes effect while the service runs. The next check is due one interval after service startup or the last completed worker. Shortening the interval starts a check immediately if it is overdue. Checks never overlap.

### Live configuration

Foreground relays and services watch the selected configuration file. Both direct edits and `config set` or `config clear` apply while the process runs. The watcher waits about 300 milliseconds for a save to settle; polling every two seconds catches missed events. The whole file must pass validation and any configured discovery before changes apply. Missing, incomplete, invalid, or undiscoverable settings leave the last good configuration active and produce a warning without logging file contents.

| Setting | Running behavior |
| --- | --- |
| `hostname`, `publicUrl`, `autoDiscovery` | New registration and renewal responses advertise the new callback URL. A changed public URL path becomes the callback route. |
| `port`, `bind` | Kestrel drains requests and rebinds in the same process. Connections may be interrupted. If binding fails, ORelay attempts to restore the old listener and keeps the previous configuration. |
| `leaseSeconds` | New registrations and renewals use the new duration. Existing expiry times stay unchanged until renewal. |
| `maxRegistrations` | New registrations use the new limit. Lowering it does not evict existing registrations. |
| `autoUpdate`, `autoUpdateIntervalSeconds` | A native service starts, stops, or reschedules future update checks. An installation already underway finishes normally. |

Invocation flags still override saved values after a reload. Changing a bind from shared to loopback also blocks non-loopback callback destinations, including existing registrations, without deleting their rows or extending their leases. Changing it back permits those registrations again if they have not expired.

Save related edits together when they must apply as one change. A rejected listener change also rejects other settings in that save. Correct and save the file again to retry. If the previous listener cannot be restored, ORelay logs an error requiring a configuration correction or restart. `config get` shows saved settings; it does not prove that a running listener accepted them.

Changing an advertised URL does not update provider redirect URI registrations or client configuration. Update those separately when needed. Reloading cannot switch the selected configuration path or registration database; those belong to the process invocation.

For `tailscale`, an unset hostname is discovered instead of using the `localhost` default. If an existing file contains `hostname`, it remains an explicit override. Use `orelay config clear hostname` to allow discovery after selecting `tailscale`.

`publicUrl` takes precedence over `hostname`. It can be a base URL, with `/callback` appended, or a complete URL ending in `/callback`. A path prefix is preserved. It must use HTTP or HTTPS without credentials, a query, or a fragment. ORelay itself serves HTTP; an HTTPS public URL requires an operator-managed TLS endpoint that forwards to it.

## Foreground server

```text
orelay server
orelay server --port 13000
orelay --config-file ./orelay.shared.json server --bind 0.0.0.0 --hostname relay.example.test
```

Effective settings use built-in defaults, then saved values, then command-line flags. On the first start, a missing file is created with the supplied flags. When the file exists, server flags apply only to that process and are not saved. Use `config set` for permanent changes. Valid saved changes apply while the process runs, with command-line flags still taking precedence. Rejected changes keep the previous settings; see [live configuration](#live-configuration).

A wildcard bind needs a usable advertised address. A bind address controls listening; an advertised hostname or public URL controls what the provider and browser use. Setting a hostname does not make a loopback listener remotely reachable.

Stop the foreground process with Ctrl+C. The server persists registration IDs, destinations, and UTC lease expiry in a SQLite file beside the selected configuration. For `orelay.json`, the database is `orelay.registrations.db`. Use separate configuration paths for independent relays. Keep this directory writable by the service account and retain the database during upgrades. SQLite is compiled into the native executable.

Registrations and renewals commit before the relay acknowledges them. Deletions and expiry cleanup are durable. Live registrations survive a restart with their original expiry; the clock continues while the server is stopped. Unknown, deleted, or expired registrations still fail. A corrupt or incompatible database prevents startup rather than starting with an empty registry. The relay never persists codes or tokens, and normal server logs omit callback query values.

Server logs go to stderr. Each line has a local timestamp and a level, coloured in an interactive terminal. Registration and callback messages identify a worktree by the first eight characters of its registration ID and its destination origin, such as `http://localhost:5017`. Destination paths and callback query values are omitted. Health probes and successful lease renewals stay quiet.

Redirecting stderr produces plain text without colour codes. Set `NO_COLOR=1` or `TERM=dumb` to disable colour in a terminal. With `server --json`, stdout contains the startup readiness object and stderr contains one JSON object per log event. Colour is disabled in JSON mode.

The server and automatic updater also write plain-text UTF-8 logs under `logs` beside the selected configuration file. `orelay.log` is the current file, `orelay.1.log` is the previous file, and `orelay.2.log` is the oldest. There are at most three log files, each capped at 2 MiB, or 2,097,152 bytes. Before a write would exceed that size, ORelay removes the oldest file and rotates the others. Logs append across restarts. Configurations in the same directory share these files and their retention limit.

File records have a UTC timestamp, process ID, level, application category, and event ID. Startup records identify the version and service or foreground mode. Update schedules show the service, interval, and next check time. Update attempts log release checks, downloads, checksum verification, replacement, restart, recovery, and a final status with the service, versions, elapsed time, and a safe reason code. Callback records confirm that ORelay issued a redirect; they do not confirm delivery to the worktree.

Framework logs, exception details, scopes, callback query values, and destination paths are excluded from file output. If file logging is unavailable, ORelay emits a fixed warning to stderr and keeps running; later writes retry. The service account must be able to create and write the `logs` directory.

Commands that can change state also append to these files: `init`, `setup`, `config set`, `config clear`, `doctor --fix`, `install`, `update`, and service actions other than `status`. Entries record the operation, start, completion or failure, exit code, and elapsed time. Completion means the command returned successfully; it does not imply that settings changed, since setup can be skipped or cancelled. Manual installs and updates include replacement events, versions, whether the executable changed, and a safe error code. Raw arguments, configuration values, paths, and command output are not copied into the log.

Read-only commands such as `config get`, `doctor` without `--fix`, `service status`, and `update --check` do not create or append logs. Help, version output, and commands rejected by argument parsing also stay quiet. A foreground server keeps its normal server logs.

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
orelay --config-file <absolute-config-path> service enable
orelay --config-file <absolute-config-path> service disable
orelay --config-file <absolute-config-path> service uninstall
```

Install from a stable executable location with the selected config already initialized. Service management requires the platform's administrative privileges. Windows uses the service control manager; Linux uses a system-level systemd unit. macOS supports foreground execution, with no service installer in this version.

The installed definition records absolute executable and config paths. Use the same paths for later management commands. Conflicting definitions are reported instead of overwritten. Uninstall preserves the config, registration database, and executable. A service restart preserves unexpired registrations just like a foreground restart.

Use `--name <name>` on service commands and doctor to select another service identity. Windows defaults to `ORelay`, running as LocalSystem with demand start. Linux defaults to `orelay.service`, running as root. Install creates the definition but does not start it or enable boot-time startup. `service enable` selects automatic boot startup; `service disable` restores manual startup without stopping the service. Grant the service account access to the chosen configuration directory.

## Installation and updates

```text
orelay install --install-dir <path>
orelay update --check --json
orelay update
orelay --config-file <absolute-config-path> update --restart-service --name <service-name>
```

`install` copies the running native executable to a stable directory. The default is `%LOCALAPPDATA%\ORelay` on Windows and `~/.local/bin` on Unix. It preserves existing configuration and registration data. Add the directory to your `PATH`; the command does not edit shell profiles or install a service. The repository's `install.ps1` and `install.sh` bootstraps download a verified release and invoke this command.

An existing executable with the same version is skipped only when its SHA-256 matches the source. Different builds of the same version are replaced; newer versions are never implicitly downgraded. The installer reads the existing file's identity and version metadata without executing it. Missing, malformed, or ambiguous metadata leaves that file untouched; choose an empty installation directory in that case. Linked executable, directory, and lock-file paths are rejected before locking the destination.

After installation, the bootstrap runs `setup --if-needed` from the installed executable when a terminal is available. Existing configuration is preserved on repeat installs. Pass `-Defaults` to the PowerShell script or `--defaults` to the shell script to accept local foreground defaults without questions. Add `-AddToPath` or `--add-to-path` to configure user PATH unattended, or use `-SkipPath` or `--skip-path` to suppress the interactive offer. Pass `-SkipSetup` or `--skip-setup` to install only; it cannot be combined with PATH options. Without a terminal, setup is skipped unless defaults were explicitly requested; an explicit PATH addition requires defaults in that case. Script options `-ConfigFile`/`--config-file` and `-Name`/`--name` also apply to setup.

Both bootstrap scripts require release `0.2.0` or later. The PowerShell script returns to your prompt on completion. Failures throw an error that includes the native command's exit code; running the script with `-File` returns process exit code `1` on failure.

`update --check` reads the latest stable GitHub release. `update` verifies the matching archive checksum and executable version before replacing the installed binary. Development builds are never implicitly downgraded to an older stable release. Managed `dotnet run` builds do not support installation or updates.

`--restart-service` explicitly permits restarting the selected service. Supply its original `--config-file` and custom `--name` when applicable. An initially stopped service stays stopped. A running service must pass startup checks after replacement; failures attempt to restore the previous executable and service state. Configuration and database files are not rolled back. Database schema changes in future releases must therefore preserve executable rollback compatibility.

Rollback handles failures detected by the updater. A forced termination or power loss between the file moves can interrupt recovery. If the executable is missing afterward, check its directory for `<executable>.backup-*` and restore the previous executable to its original name before restarting the service.

Automatic service updates run every three hours by default. Set `autoUpdate` to `false` to disable them, or set `autoUpdateIntervalSeconds` in the service's selected configuration file to choose another interval. Existing saved values remain in effect until changed or cleared. The running service applies valid changes to its update schedule automatically; see [live configuration](#live-configuration) for rejected changes and interval timing. Checks use the same stable-release feed, checksum validation, version checks, and rollback as `update`. Disabled configurations and foreground servers do not start automatic workers. A missing or invalid configuration prevents an automatic update.

The worker runs independently so it can finish replacing and restarting the relay after the relay stops. It uses the service account's permissions on Windows. Linux uses a separate transient systemd service and requires systemd 254 or later with permission to launch it. Neither platform prompts for elevation. Permission, network, or validation failures leave callback serving active and are retried after the interval.

The latest worker result is saved as `<config-stem>.auto-update.json` beside the selected configuration. It contains the completion time and updater result. Set `autoUpdate` to `false` to stop scheduling further checks. Workers check the saved value again before updating; this does not interrupt installation or rollback already underway.

Private release access uses `GH_TOKEN`, `GITHUB_TOKEN`, or the current GitHub CLI login. Public releases can be downloaded without credentials. Credentials are never saved in relay configuration. Both commands accept `--json`, keep diagnostics on stderr, and use the exit codes below.

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
