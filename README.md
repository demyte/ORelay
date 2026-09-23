# ORelay

ORelay routes OAuth 2 authorization callbacks to development worktrees. Register one fixed callback URL with a provider such as Xero, then run multiple worktrees on different ports. Each worktree gets its own temporary registration ID.

```text
Provider → ORelay /callback → browser redirect → the registered worktree
```

The worktree validates state and exchanges the authorization code directly with the provider. It owns its tokens and refresh logic. Use the fixed relay `redirect_uri` for both the authorization request and token exchange. Provider consent and grant rules still apply; separate worktrees do not guarantee separate provider grants.

## Run

ORelay is a .NET 10 application published as a self-contained Native AOT executable. The published executable runs without installing .NET. See [versions and releases](docs/releases.md) for tag-driven downloads and the GitHub Packages feed, and [native builds and platform verification](docs/native-platforms.md) for the platform matrix and build prerequisites. The [v1 handoff](docs/v1-handoff.md) records the initial implementation and verification.

```text
orelay init
orelay server --port 12987 --bind 127.0.0.1
orelay doctor
```

Register `http://localhost:12987/callback` with your provider if its redirect-URI policy allows that address. The browser completing authorization must be able to reach both ORelay and the destination worktree. ORelay returns a browser redirect; it does not make a server-to-server callback request.

The server, `init`, and `doctor --fix` create a missing `orelay.json` beside the executable with these defaults, plus any supplied setting overrides. Existing files are preserved.

```json
{
  "schemaVersion": 1,
  "port": 12987,
  "bind": "127.0.0.1",
  "hostname": "localhost",
  "autoDiscovery": "none",
  "leaseSeconds": 300,
  "maxRegistrations": 1000
}
```

Use a writable path when running from a protected installation directory, a service, or a container:

```text
orelay --config-file /data/orelay.json init --port 12987
orelay --config-file /data/orelay.json server
```

Run `orelay --help` or append `--help` to a command. Commands run without interactive prompts. [CLI reference](docs/cli.md) covers settings, JSON output, exit codes, and services.

Server logs show timestamps and coloured levels for startup, registrations, callback routing, rejections, and shutdown. They go to stderr, with plain text when redirected. Set `NO_COLOR=1` to disable colour in a terminal, or use `server --json` for JSON logs. Callback state, codes, and provider error values are never included.

## Register a worktree

Send `POST /registrations` with an exact HTTP or HTTPS destination:

```json
{"callbackUrl":"http://127.0.0.1:5017/oauth/callback"}
```

The response includes `id`, `relayCallbackUrl`, `leaseSeconds`, and `expiresAt`. Construct OAuth state as `<id>.<your-random-state>`. Store and validate that complete state value in the worktree, including normal browser correlation and one-time consumption. ORelay uses only the ID prefix to select a destination.

When the provider redirects to ORelay, it forwards the complete query unchanged, including state, code, errors, repeated fields, and encoded values. Unknown or expired registrations fail without a fallback destination.

| Request | Effect |
| --- | --- |
| `POST /registrations` | Create a fresh registration and lease. |
| `PUT /registrations/{id}/lease` | Renew a live registration; expired IDs return 404. |
| `DELETE /registrations/{id}` | Deregister; repeated deletion succeeds. |
| `GET /callback` | Redirect the browser using the ID in state. |
| `GET /health` | Read the relay identity and health. |

Registrations exist only in memory. The default lease is five minutes. Renew before expiry and deregister on shutdown. Lease expiry removes registrations left by crashed processes. Restarting ORelay loses every registration; affected applications need a fresh registration, and pending OAuth flows must start again.

See the [HTTP and state contract](docs/protocol.md) for request and response fields, state bounds, destination restrictions, and errors.

## Aspire

`ORelay.Aspire.Hosting` is a separate NuGet library used by the consuming AppHost. Start ORelay separately, then connect your application resource:

```csharp
using ORelay.Aspire.Hosting;

var relay = builder.AddORelay("relay", new Uri("http://127.0.0.1:12987"));
builder.AddProject<Projects.Api>("api")
    .WithORelay(relay, "/oauth/callback", endpointName: "http");
```

The AppHost registers before starting the application and injects `ORelay__RegistrationId` and `ORelay__RedirectUri`. It renews the lease even while the application is paused, and deregisters on resource or AppHost shutdown. Each registered resource has one instance; separate worktrees run separate AppHosts.

In the Aspire dashboard, open the relay resource to inspect each application's registration status, callback destination, public relay callback, last successful renewal, lease expiry, and renewal interval. Resource relationships link the applications to their relay. The relay's health check summarizes its registrations, while each application's health check reports its own registration. The relay console logs registration, successful renewal, connection failures, registration loss, and cleanup, with the application name on each entry. Callback queries and OAuth values are not included.

If a registration is lost, the resource reports degraded health with a restart instruction. Explicitly restart the resource or AppHost to obtain a new ID. ORelay does not restart applications automatically. See [package usage](src/ORelay.Aspire.Hosting/PACKAGE.md) and the [sample with a synthetic OAuth provider](samples/GUIDE.md).

## Shared relay

The default listener and allowed destinations are loopback-only. To share a relay, explicitly bind a reachable interface and supply an advertised hostname or URL:

```text
orelay server --bind 0.0.0.0 --hostname relay.example.test
orelay server --bind 0.0.0.0 --auto-discovery tailscale
```

Tailscale discovery uses the local Tailscale CLI. An explicit public URL or saved hostname takes precedence. For an existing configuration containing `hostname`, use `orelay config clear hostname` when switching to Tailscale discovery. Discovery chooses an address; it does not change firewall rules, application bindings, or provider registrations. Use `doctor` to check the resulting configuration.

Management authentication is deferred in this version. Every client that can reach the management API can create, renew, or delete registrations. Shared binding also permits remote callback destinations. Choose network access accordingly.

## Build and contribute

Install the SDK pinned in `global.json`. From the checkout, these PowerShell scripts work on Windows, Linux, and macOS with PowerShell 7:

```powershell
./scripts/build.ps1
./scripts/format.ps1
./scripts/test.ps1
./scripts/publish.ps1 -RuntimeIdentifier win-x64
```

Use the matching host and native compiler prerequisites when publishing for another RID. Run a focused test selection with `dotnet test tests/ORelay.Tests --filter FullyQualifiedName~Configuration`. Aspire integration tests require a running, run-owned relay; the sample guide gives the commands.

The [v1 tickets](.scratch/orelay-v1/issues/README.md) track delivery and verification. ORelay is licensed under the [MIT license](LICENSE).
