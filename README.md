# ORelay

One OAuth callback URL for all your development worktrees.

ORelay routes OAuth 2 authorization callbacks to the worktree that started the flow. Register one fixed callback URL with a provider such as Xero, then run multiple worktrees on different ports. Each worktree gets its own temporary registration ID.

```text
Provider → ORelay /callback → browser redirect → the registered worktree
```

The worktree validates state, exchanges the authorization code, and owns its tokens. ORelay handles callback routing. Provider consent and grant rules still apply; separate worktrees do not guarantee separate provider grants.

ORelay ships as one native executable for Windows, Linux, and macOS, on x64 and ARM64. It includes SQLite and runs without installing .NET or SQLite. A separate `ORelay.Aspire.Hosting` library connects applications managed by Aspire.

## Install

Run the command for your platform. The installer downloads the matching executable and verifies its checksum. No .NET installation, GitHub account, or GitHub CLI is required.

The installer, guided setup, self-updater, and persistent registrations require version `0.2.0` or later.

### Windows · PowerShell

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/demyte/ORelay/main/install.ps1)))
```

### macOS and Linux · Shell

```sh
curl -fsSL https://raw.githubusercontent.com/demyte/ORelay/main/install.sh | sh
```

Installs to `%LOCALAPPDATA%\ORelay` on Windows or `~/.local/bin` on macOS and Linux. Setup offers to add that directory to your user `PATH`. After accepting, open a new terminal to run `orelay` commands from any folder. Rerun the installer to upgrade; existing configuration and registration data are preserved.

On first installation in a terminal, setup shows what the defaults mean and offers **Go with defaults** or **Customize**. Defaults keep ORelay local on port `12987`, with no background service. Customize to choose a port, bind address, LAN or Tailscale access, and service startup on Windows or Linux. Confirm the summary to save. Run `orelay setup` again to change these choices. Existing settings are preserved when you rerun the installer.

For unattended installation, use `-Defaults` with the PowerShell script or `--defaults` with the shell script. Add `-AddToPath` or `--add-to-path` to configure PATH too. `-SkipPath` or `--skip-path` leaves PATH unchanged; `-SkipSetup` or `--skip-setup` installs only. See [setup options](docs/cli.md#setup) for unattended custom configuration.

<details>
<summary>Installation options and manual downloads</summary>

To choose another directory, download the [PowerShell installer](install.ps1) and run it with `-InstallDir <path>`, or the [shell installer](install.sh) with `--install-dir <path>`. The Unix installer requires `curl`, `tar`, and a SHA-256 tool available on the supported systems.

For manual installation, download the archive for your OS and architecture from [Releases](https://github.com/demyte/ORelay/releases), verify its `.sha256` file, and extract it. Run the extracted executable with `install --install-dir <path>` to put it in a stable location. The [platform guide](docs/native-platforms.md) lists tested OS versions.

</details>

## Quick start

```text
orelay setup
orelay server
```

In another terminal, run `orelay doctor`. Register `http://localhost:12987/callback` with your OAuth provider if its redirect-URI policy allows that address. Use this same `redirect_uri` for authorization and code exchange.

The defaults are port `12987`, a loopback listener at `127.0.0.1`, a five-minute lease, and capacity for 1,000 registrations. The browser completing authorization must be able to reach both ORelay and the destination worktree.

`init` and the server create a missing `orelay.json` beside the executable. For a service or a protected installation directory, select a writable configuration path:

```text
orelay --config-file /data/orelay.json init
orelay --config-file /data/orelay.json server
```

Registrations are stored in a sibling SQLite file, such as `/data/orelay.registrations.db`. Updates preserve that file. Unexpired registrations survive relay restarts; expiry continues while the relay is stopped. Applications must restart explicitly if their lease expires or their registration is deleted.

The running relay watches its selected configuration file. Valid edits apply automatically, including hostname, port, bind address, lease policy, and update scheduling. A listener change briefly interrupts connections. Invalid edits keep the previous settings, and command-line overrides still take precedence. See [live configuration](docs/cli.md#live-configuration) for details.

See the [CLI reference](docs/cli.md) for configuration, logging, JSON output, and exit codes. Use `setup` for guided configuration or `setup --defaults --yes` for unattended defaults.

## Use with Aspire

Add `ORelay.Aspire.Hosting` to your AppHost. The package is public, but GitHub's NuGet registry still requires a token to restore it. See the [package feed instructions](docs/releases.md#github-packages) for authentication and version selection. Start ORelay separately, then connect an application resource:

```csharp
using ORelay.Aspire.Hosting;

var relay = builder.AddORelay("relay", new Uri("http://127.0.0.1:12987"));
builder.AddProject<Projects.Api>("api")
    .WithORelay(relay, "/oauth/callback", endpointName: "http");
```

The AppHost registers before starting the application and injects `ORelay__RegistrationId` and `ORelay__RedirectUri`. It renews the lease while the application runs or is paused, and deregisters on resource or AppHost shutdown. Separate worktrees run separate AppHosts.

The Aspire dashboard shows registration health, destination, callback URL, lease expiry, and renewal activity. If a registration is lost, it reports degraded health with an explicit restart instruction. It retries a disconnected relay while the lease remains valid.

See [package usage](src/ORelay.Aspire.Hosting/PACKAGE.md) and the [sample with a synthetic OAuth provider](samples/GUIDE.md).

## Use the HTTP API

Send `POST /registrations` with an exact HTTP or HTTPS destination:

```json
{"callbackUrl":"http://127.0.0.1:5017/oauth/callback"}
```

The response includes `id`, `relayCallbackUrl`, `leaseSeconds`, and `expiresAt`. Construct OAuth state as `<id>.<your-random-state>`. Store and validate that complete value in the worktree, including browser correlation and one-time consumption. ORelay uses only the ID prefix to select a destination.

| Request | Effect |
| --- | --- |
| `POST /registrations` | Create a registration and lease. |
| `PUT /registrations/{id}/lease` | Renew a live registration; expired IDs return 404. |
| `DELETE /registrations/{id}` | Deregister; repeated deletion succeeds. |
| `GET /callback` | Redirect the browser using the ID in state. |
| `GET /health` | Read relay identity and health. |

Callbacks preserve the complete query, including state, code, errors, repeated fields, and encoded values. Unknown or expired IDs fail without a fallback destination. ORelay stores routing destinations and lease expiry, never callback queries, codes, or tokens. Renew before expiry and deregister on shutdown.

See the [HTTP and state contract](docs/protocol.md) for fields, validation, and errors.

## Update

```text
orelay update --check
orelay update
```

The updater selects the latest stable release for the executable's platform, verifies its checksum and version, and replaces the executable. It preserves configuration and the registration database. It refuses automatic downgrades.

For a running Windows or Linux service, explicitly allow a restart and use the same configuration and service name as the installation:

```text
orelay --config-file <absolute-config-path> update --restart-service --name <service-name>
```

A short restart preserves live registrations. A lease that expires during an outage stays expired. The updater checks startup and restores the previous executable if replacement or startup fails.

Automatic updates are enabled for installed Windows and Linux services. To choose a different check interval:

```text
orelay --config-file <absolute-config-path> config set autoUpdateIntervalSeconds 86400
```

The service checks for stable releases every three hours by default and restarts itself to apply an update. This example sets a one-day interval. Set any interval from `60` to `2592000` seconds. Changing the interval takes effect while the service runs. The next check is due one interval after service startup or the last completed check, and runs immediately if already overdue. Foreground runs never check automatically. Set `autoUpdate` to `false` to disable checks. Existing saved `false` values and custom intervals remain in effect until you change or clear them.

To limit automatic updates, run `orelay --config-file <absolute-config-path> config set autoUpdateLevel patch`. `patch` accepts newer stable releases within the installed major and minor version. `minor` accepts minor and patch updates within the installed major version. `major` accepts any newer stable release and is the default for new configurations and missing values. The updater checks the latest stable release and skips it if it exceeds this limit. Manual `update` commands still accept any newer stable release.

The service account needs permission to replace the executable and restart its service. Linux also requires `systemd-run` from systemd 254 or later. The latest check or installation result is written beside the selected config, for example `orelay.auto-update.json`. A worker rereads `autoUpdate` and `autoUpdateLevel` before installation; an installation already underway finishes normally.

Relay activity and automatic update actions are logged under `logs` beside the selected configuration. ORelay keeps `orelay.log` and two backups, each limited to 2 MiB. Update logs include release checks, downloads, replacement, restart, rollback, and the final outcome. See the [CLI reference](docs/cli.md#foreground-server) for log contents and retention.

## Run as a service

Windows Service and Linux systemd support use the installed executable. Initialize the configuration, then run with the platform's administrative privileges:

```text
orelay --config-file <absolute-config-path> service install
orelay --config-file <absolute-config-path> service start
orelay --config-file <absolute-config-path> service status
```

Installation does not start the service or enable boot-time startup. macOS supports foreground execution. See [service commands](docs/cli.md#services) for names, accounts, and lifecycle operations.

## Share a relay

The default listener and allowed destinations are loopback-only. To share a relay, explicitly bind a reachable interface and supply its advertised hostname:

```text
orelay server --bind 0.0.0.0 --hostname relay.example.test
```

Tailscale discovery is also available through `--auto-discovery tailscale`. A saved hostname takes precedence; clear it with `orelay config clear hostname` when switching to discovery. This does not change firewall rules or provider registrations.

Management authentication is deferred. Every client that can reach the management API can create, renew, or delete registrations. Shared binding also permits remote callback destinations. Choose network access accordingly.

## Build and contribute

Install the SDK pinned in `global.json` and PowerShell 7. From the checkout:

```powershell
./scripts/build.ps1
./scripts/format.ps1
./scripts/test.ps1
./scripts/publish.ps1 -RuntimeIdentifier win-x64
```

Native publishing compiles and statically links the pinned SQLite source. Use the matching host and the compiler prerequisites in the [native build guide](docs/native-platforms.md). Published executables have no .NET or SQLite runtime dependency.

Run focused tests with `dotnet test tests/ORelay.Tests --filter FullyQualifiedName~Configuration`. The [sample guide](samples/GUIDE.md) covers Aspire integration tests. The [release guide](docs/releases.md) explains version tags, downloads, and package publication.

## License

[MIT](LICENSE). SQLite is in the public domain.
