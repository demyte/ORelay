# Native artifacts and services

## Sub-features

- Publish a self-contained single-file Native AOT executable.
- Execute the artifact without a runtime beside it.
- Install, enable or disable boot startup, start, inspect, restart, stop, and uninstall an owned Windows or systemd service on a disposable host.
- Report platform coverage by actual runner and RID.

## How to get to it (user POV)

Publish a matching RID, then run the executable from its output directory:

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1 -RuntimeIdentifier win-x64 -Configuration Release
& .\artifacts\publish\win-x64\orelay.exe --version
```

The service command surface is `orelay service install|enable|disable|start|stop|restart|status|uninstall`. Select a run-owned identity with `--name` and a run-owned absolute `--config-file`. Implementations are in `src/ORelay/Services/WindowsServiceManager.cs` and `SystemdServiceManager.cs`; platform installation needs administrator or service-manager privileges and is not run by the routine helper.

## Driving it with PowerShell

Run `.agents/skills/verify-orelay/scripts/verify.ps1` against the selected artifact. It records the directory inventory, SHA-256, CLI output, server process, HTTP behavior, and cleanup. The checked-in workflow `.github/workflows/native-smoke.ps1` supplies the CI smoke recipe for each published RID. Read `docs/native-platforms.md` before interpreting a cross-platform result.

Focused service tests are:

```powershell
dotnet test tests/ORelay.Tests --filter FullyQualifiedName~WindowsServiceManager
dotnet test tests/ORelay.Tests --filter FullyQualifiedName~SystemdServiceManager
```

These tests exercise injected service boundaries. They do not install a real service. A real service pass must use a run-owned name and executable/configuration path, record the manager state, and remove only that owned service.

On a matching disposable privileged host, first run `.github/workflows/build-service-fixtures.ps1 -RunRoot <unique-run-directory> -Rid <win-x64-or-linux-x64> -ExecutableName <orelay-or-orelay.exe> -ProductionExecutablePath <absolute-native-path>`. Then invoke `.github/workflows/service-smoke.ps1` with `-ExecutablePath` for production, `-PreviousExecutablePath`, `-NextExecutablePath`, and `-FailingExecutablePath` from that run's `service-fixtures` directory, plus `-RunRoot`, `-Rid`, and `-RequireServiceProof`. CI supplies these paths. The script checks a conflicting definition, repeated operations, manual boot by default, enable and disable through the native service manager, paths with spaces and a literal dollar sign, real callback delivery, registration persistence after restart, different-version replacement while running and stopped, failed-start rollback, preserved config, and final service removal. The x64 CI jobs require this proof; unavailable privileges or systemd fail the job after recording the prerequisite.

CI also checks installation without privileges. On Windows the script creates a temporary non-admin account, grants access only to its run directory, and removes the account and grant afterward. On Linux it invokes installation as the ordinary runner user. Both must return `PermissionDenied` with exit code 3 and leave the proposed service absent. Keep this machine-state proof on disposable CI hosts.

The native smoke script's `-HideRuntimeForProof` switch is restricted to GitHub Actions. It validates and temporarily moves the selected installation's `shared` directory, proves a managed control fails for the missing runtime, runs the native executable, and restores the directory in `finally`. Inspect the positive/negative control records and `runtime-restore.json` before claiming runtime independence. Do not use this switch on a developer machine.

## Gotchas

- A Windows Native AOT run does not prove Linux or macOS support.
- A cross-publish does not prove execution on the target operating system.
- Keep `orelay.json` outside the publish directory.
- Service operations can change machine state. Do not run install, start, stop, restart, or uninstall from routine relay verification without an explicit disposable service plan.
