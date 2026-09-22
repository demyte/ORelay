# Aspire hosting package

## Sub-features

- Start a consuming AppHost with `WithORelay`.
- Register the API callback before the API begins serving.
- Renew the registration while the resource stays alive.
- Surface registration loss and require an explicit resource or AppHost restart.
- Exercise the package from a local NuGet artifact as well as a project reference.

## How to get to it (user POV)

Start a run-owned relay, then run the sample AppHost. Open the API's `/login` path and follow the synthetic provider flow. The API's `/sample/session` response shows the registration ID, fixed relay redirect URI, startup time, and process ID.

From the repository root, the checked sample commands are:

```powershell
dotnet build samples/ORelay.Sample.AppHost
dotnet run --project samples/ORelay.Sample.AppHost --no-build -- --RelayUrl http://127.0.0.1:12987
```

For the package path, use a fresh local version, `samples/NuGet.Local.Config`, and the commands in `samples/GUIDE.md`.

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

Set `ORELAY_TEST_RELAY_BINARY` to the absolute native executable when running the real-relay restart case. The repository's existing package proof is under `.artifacts/verification/aspire-package/`. This skill does not count that proof as a new run unless the commands are executed again.

## Gotchas

- DCP must be installed and usable for the integration path.
- The relay and AppHost need separate run-owned ports and configs.
- A lost registration requires an explicit restart. The sample does not change a running process's environment.
- A project reference and a packed local NuGet package test different dependency paths. Record which one ran.
