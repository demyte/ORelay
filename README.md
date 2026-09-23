# ORelay

One OAuth callback URL for all your development worktrees.

ORelay routes OAuth 2 authorization callbacks to the worktree that started the flow. Register one fixed callback URL with a provider such as Xero, then run multiple worktrees on different ports. Each worktree gets its own temporary registration ID.

```text
Provider → ORelay /callback → browser redirect → the registered worktree
```

The worktree validates state, exchanges the authorization code, and owns its tokens. ORelay handles callback routing. Provider consent and grant rules still apply; separate worktrees do not guarantee separate provider grants.

ORelay ships as one native executable for Windows, Linux, and macOS, on x64 and ARM64. It includes SQLite and runs without installing .NET or SQLite. A separate `ORelay.Aspire.Hosting` library connects applications managed by Aspire.

## Install

The bootstrap scripts detect your platform, verify the release checksum, and install the executable. Running the bootstrap again also upgrades installations that predate `orelay update`. They preserve existing configuration and registration data.

> The bootstrap, self-updater, and persistent registrations are new in this checkout. Published versions through `v0.1.2` do not include them. The commands below need the first release containing these changes.

The repository and release downloads are public. No GitHub account, token, or GitHub CLI is required to install the executable.

### Windows

Run in Windows PowerShell or PowerShell 7:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/demyte/ORelay/main/install.ps1 -OutFile install.ps1 -ErrorAction Stop
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

The default directory is `%LOCALAPPDATA%\ORelay`. Add it to your user `PATH`, or run `& "$env:LOCALAPPDATA\ORelay\orelay.exe"` directly. Use `-InstallDir <path>` to select another directory.

### Linux and macOS

```sh
curl -fsSL https://raw.githubusercontent.com/demyte/ORelay/main/install.sh -o install.sh && sh install.sh
```

The default directory is `~/.local/bin`. Add it to your `PATH` if needed. Use `--install-dir <path>` to select another directory. The Unix bootstrap requires `curl`, `tar`, and a SHA-256 tool available on the supported systems.

You can install without cloning or building ORelay. An existing GitHub CLI login can also download the release. The self-updater accepts `GH_TOKEN` or `GITHUB_TOKEN` when authenticated access is useful.

For manual installation, download the archive for your OS and architecture from [Releases](https://github.com/demyte/ORelay/releases), verify its `.sha256` file, and extract it. Run the extracted executable with `install --install-dir <path>` to put it in a stable location. The [platform guide](docs/native-platforms.md) lists tested OS versions.

## Quick start

```text
orelay init
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

See the [CLI reference](docs/cli.md) for configuration, logging, JSON output, and exit codes. All commands run without interactive prompts.

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

A short restart preserves live registrations. A lease that expires during an outage stays expired. The updater checks startup and restores the previous executable if replacement or startup fails. Automatic background installation is planned separately.

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
