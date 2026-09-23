# Aspire consumer sample

The AppHost connects to a separately running ORelay server. It launches an API and a synthetic OAuth provider. Open the API's `/login` endpoint to complete authorization, full-state validation, and direct code exchange. `/sample/session` exposes the registration ID, fixed relay redirect URI, startup time, and process ID for verification. No live provider credentials are needed.

From the repository root, build and run with project references:

```powershell
dotnet build samples/ORelay.Sample.AppHost
dotnet run --project samples/ORelay.Sample.AppHost --no-build -- --RelayUrl http://127.0.0.1:12987
```

The provider expects the server's `/callback` URL. Supply `--RelayRedirectUri` if the relay advertises another fixed URL. `--ApiPort` selects an API port; otherwise Aspire allocates one. Choose the API's HTTP URL from the Aspire dashboard and append `/login`.

The sample also accepts `--CallbackUrl` for an exact callback destination, `--CallbackHostname` to replace only the allocated endpoint's host, and `--CallbackDiscovery Tailscale` to discover the AppHost machine's Tailscale address. The exact URL wins over hostname and discovery; a hostname wins over discovery. `Local` is the default discovery mode. A remote hostname requires a reachable listener. The integration rejects a remote host when Aspire reports that the allocated endpoint binds only to loopback. These options do not open network interfaces or prove browser reachability.

To prove local NuGet consumption, choose a fresh prerelease version for each changed package to avoid reusing a previous cached package:

```powershell
dotnet pack src/ORelay.Aspire.Hosting -o .artifacts/packages -p:MinVerVersionOverride=0.1.0-local.1
dotnet build samples/ORelay.Sample.AppHost -p:UseORelayPackage=true -p:ORelayPackageVersion=0.1.0-local.1 --configfile samples/NuGet.Local.Config
dotnet run --project samples/ORelay.Sample.AppHost --no-build -p:UseORelayPackage=true -p:ORelayPackageVersion=0.1.0-local.1 -- --RelayUrl http://127.0.0.1:12987
```

The package is referenced only by the AppHost. Neither API nor provider references it. The API reads `ORelay:RegistrationId` and `ORelay:RedirectUri` at startup. It generates a new random state for each flow, stores the complete registration-ID envelope, correlates it with a browser cookie, and consumes it once on callback. The code exchange sends the same fixed relay redirect URI used for authorization.

When an AppHost reports a lost registration, stop beginning new flows and explicitly restart the API resource or AppHost. The resource health check and resource log explain this requirement. Restarting supplies a fresh registration ID; pending old flows fail. The sample has no mechanism for changing a running process's environment.

Select `relay` in the Aspire dashboard to inspect the API's registration status, public callback, forward destination, last successful renewal, lease expiry, and renewal interval. Its console logs report successful renewals and lifecycle failures with the application name. The relay's health summarizes this AppHost's registrations; the API's health check still reports its own registration. A relationship connects the API to the relay. The relay server runs separately, so its process console is not streamed into this resource.

Focused lifecycle tests run with:

```powershell
dotnet test tests/ORelay.Aspire.Hosting.Tests --filter Category!=AspireIntegration
```

The real AppHost integration test starts two AppHosts and drives their public HTTP authorization paths. Use a run-owned relay with a short lease, such as six seconds. The test deletes only registrations created by its AppHosts and stops those AppHosts when finished. It checks registration loss, visible degraded health, explicit resource restart, isolation, and cleanup. On Windows it suspends the actual owned API process beyond one full lease and proves AppHost renewal keeps the registration alive, then resumes the API in a `finally` block. Without `ORELAY_TEST_SERVER` the integration test reports a skip.

```powershell
$env:ORELAY_TEST_SERVER = 'http://127.0.0.1:19387'
dotnet test tests/ORelay.Aspire.Hosting.Tests --filter Category=AspireIntegration
```

Set `ORELAY_TEST_RELAY_BINARY` to the absolute path of a built `orelay.exe` or `orelay.dll` to enable the separate real-relay restart test. It starts its own relay on an allocated port with a six-second lease and a run-owned configuration under `work/verification`. It kills and restarts only that child process, verifies an already-pending callback fails, then restarts the API resource and completes a new flow. The relay child is terminated in cleanup.

For that same test against the packed library, build the test project with `UseORelayPackage=true`, `ORelayPackageVersion`, and `--configfile samples/NuGet.Local.Config`, then run `dotnet test` with the same two properties and `--no-build --no-restore`. Both the tests and AppHost resolve the NuGet package in this mode. `obj/project.assets.json` records whether the dependency is a package or project.
