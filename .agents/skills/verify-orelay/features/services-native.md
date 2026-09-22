# Native artifacts and services

## Sub-features

- Publish a self-contained single-file Native AOT executable.
- Execute the artifact without a runtime beside it.
- Inspect the Windows service and Linux systemd command paths.
- Report platform coverage by actual runner and RID.

## How to get to it (user POV)

Publish a matching RID, then run the executable from its output directory:

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1 -RuntimeIdentifier win-x64 -Configuration Release
& .\artifacts\publish\win-x64\orelay.exe --version
```

The service command surface is `orelay service install|start|stop|restart|status|uninstall`. Its CLI dispatch and help are wired on the current branch. Implementations are in `src/ORelay/Services/WindowsServiceManager.cs` and `SystemdServiceManager.cs`; platform installation needs administrator or service-manager privileges and is not run by the routine helper. Republish the native executable after service review before treating its help or service commands as current.

## Driving it with PowerShell

Run `.agents/skills/verify-orelay/scripts/verify.ps1` against the selected artifact. It records the directory inventory, SHA-256, CLI output, server process, HTTP behavior, and cleanup. The checked-in workflow `.github/workflows/native-smoke.ps1` supplies the CI smoke recipe for each published RID. Read `docs/native-platforms.md` before interpreting a cross-platform result.

Focused service tests are:

```powershell
dotnet test tests/ORelay.Tests --filter FullyQualifiedName~WindowsServiceManager
dotnet test tests/ORelay.Tests --filter FullyQualifiedName~SystemdServiceManager
```

These tests exercise injected service boundaries. They do not install a real service. A real service pass must use a run-owned name and executable/configuration path, record the manager state, and remove only that owned service.

## Gotchas

- A Windows Native AOT run does not prove Linux or macOS support.
- A cross-publish does not prove execution on the target operating system.
- Keep `orelay.json` outside the publish directory.
- Service operations can change machine state. Do not run install, start, stop, restart, or uninstall from routine relay verification without an explicit disposable service plan.
