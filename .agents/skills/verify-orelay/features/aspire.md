# Aspire hosting package

## Sub-features

- Start a consuming AppHost with `WithORelay`.
- Register the API callback before the API begins serving.
- Renew from the AppHost, including while the API process is suspended.
- Resolve the allocated callback endpoint, with explicit URL, hostname, and optional Tailscale discovery inputs.
- Surface registration loss and require an explicit resource or AppHost restart.
- Show per-application registration details, relationships, aggregate health, and lifecycle logs on the relay resource.
- Exercise the package from a local NuGet artifact as well as a project reference.

## How to get to it (user POV)

Start a run-owned relay, then run the sample AppHost. Open the API's `/login` path and follow the synthetic provider flow. The API's `/sample/session` response shows the registration ID, fixed relay redirect URI, startup time, and process ID.

From the repository root, the checked sample commands are:

```powershell
dotnet build samples/ORelay.Sample.AppHost
dotnet run --project samples/ORelay.Sample.AppHost --no-build -- --RelayUrl http://127.0.0.1:12987
```

For the package path, use a fresh local version, `samples/NuGet.Local.Config`, and the commands in `samples/GUIDE.md`.

`WithORelay` accepts an exact `callbackUrl` or discovery options. An explicit URL wins; otherwise a hostname override or Tailscale discovery supplies the host while the allocated endpoint supplies its scheme and port. The sample exposes `--CallbackUrl`, `--CallbackHostname`, and `--CallbackDiscovery`. Known loopback-only bindings cannot advertise a remote discovered hostname. See `src/ORelay.Aspire.Hosting/CallbackDestination.cs` and the focused `CallbackDestinationTests` for this boundary.

## Driving it with PowerShell

The package's focused tests run with:

```powershell
dotnet test tests/ORelay.Aspire.Hosting.Tests --filter Category!=AspireIntegration
```

The real DCP integration path requires an owned relay and the Aspire runtime:

```powershell
$env:ORELAY_TEST_SERVER = 'http://127.0.0.1:19387'
dotnet test tests/ORelay.Aspire.Hosting.Tests --filter Category=AspireIntegration
```

Set `ORELAY_TEST_RELAY_BINARY` to the absolute native executable when running `RealRelayProcessRestartKeepsPendingFlowAndAppHostRegistration`. This test starts its own relay and real AppHost, starts an OAuth flow, kills and restarts the relay, then completes the pending flow with the same registration ID. It also checks that the SQLite file exists. Its explicit `127.0.0.1` hostname keeps the synthetic provider's callback allowlist aligned with the relay address. The repository's existing package proof is under `.artifacts/verification/aspire-package/`. This skill does not count that proof as a new run unless the commands are executed again.

The two-AppHost test also follows a callback with a valid routing ID and tampered opaque state through the relay. The worktree must return `400 invalid_state`, then the original cookie-bound flow must still complete successfully.

For dashboard visibility, inspect the relay's resource snapshot and console logs through the consuming AppHost. Verify the public callback and allocated destination, application relationship, registration status, renewal interval, and expiry. Wait for an actual lease renewal and verify that the last-renewal and expiry timestamps advance and a successful-renewal log appears under the relay resource. Delete only the owned registration and verify that relay and application health become degraded with an explicit restart instruction. Restart the application and verify recovery. Multiple applications on one relay must retain separate properties and identifiable log entries. Do not include OAuth query strings, state, codes, or tokens in dashboard properties or lifecycle logs.

To prove orphan cleanup after a crash, launch the sample AppHost as a separate recorded process against an owned relay with a short lease. Confirm its registration still routes after the initial lease duration, then kill only that AppHost's process tree without graceful shutdown. Leave the relay running and wait longer than one lease. The old ID must return 404. Start a fresh AppHost on the same API port, then another on a changed `--ApiPort`; each must receive a fresh ID while old IDs remain invalid. Capture process IDs, ports, timestamps, HTTP results, and final listener/process cleanup. This checks AppHost ownership failure, which suspending the API alone does not exercise.

## Gotchas

- DCP must be installed and usable for the integration path.
- The relay and AppHost need separate run-owned ports and configs.
- A lost or expired registration requires an explicit restart. A brief relay restart preserves a live registration through SQLite, and AppHost renewal resumes before its deadline. The sample does not change a running process's environment.
- A project reference and a packed local NuGet package test different dependency paths. Record which one ran.
- A Tailscale DNS result requires name resolution from the browser's network position. Use an explicit reachable URL or IP hostname override when MagicDNS is unavailable.
