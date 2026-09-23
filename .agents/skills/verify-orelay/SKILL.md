---
name: verify-orelay
description: Drive the published ORelay Native AOT CLI and loopback callback relay when verifying CLI, configuration, HTTP registration, lease, doctor, and native artifact behavior.
---

# Verify ORelay

Use this skill after a build or any change that affects the ORelay CLI, saved configuration, relay HTTP contract, registrations, lease lifecycle, doctor, or native publishing. The helper drives the published executable and a real loopback HTTP listener. It does not use test-only endpoints or an in-process server.

## Launch

From the repository root, publish the Windows artifact when the selected executable is stale:

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1 -RuntimeIdentifier win-x64 -Configuration Release
```

Run the verification helper:

```powershell
pwsh -NoProfile -File .\.agents\skills\verify-orelay\scripts\verify.ps1
```

The default executable is `artifacts/publish/<runtime-identifier>/orelay.exe` under the repository root, with `win-x64` as the default runtime identifier. Pass `-RuntimeIdentifier` or `-ExecutablePath` when checking another published artifact. The helper selects free loopback ports, creates a run-owned configuration, starts the executable in the foreground, waits for `GET /health` to return `{"identity":"orelay","status":"ok"}`, and stops the exact processes it started.

The relay serves `POST /registrations`, `PUT /registrations/{id}/lease`, `DELETE /registrations/{id}`, `GET /callback`, and `GET /health`. The helper uses synthetic callback values only. A callback listener accepts the redirected request and compares its raw request target with the original raw query.

## Doctor

Run the read-only doctor command directly against a known existing run-owned configuration and its live relay:

```powershell
$config = (Resolve-Path .\work\verification\<run-id>\orelay.json).Path
& .\artifacts\publish\win-x64\orelay.exe --config-file $config doctor --json
```

The selected file must already exist, and its saved port must identify the owned relay process. This command reads the configuration and probes `/health`; it must not create or rewrite the selected file. The complete helper run is a separate proof. It records the structured report, compares the selected configuration hash before and after doctor, then runs doctor against another missing run-owned path and expects exit code `1` without creating that file. `doctor --fix` is not part of this routine because it writes configuration. Test it separately against a disposable path and retain its output.

If doctor reports an occupied port or an unreachable health endpoint, first check the recorded process ID and port in the evidence directory. Never stop a process by the `orelay` process name. The helper only stops the `Process` instances it started.

## Drive

The main helper is the maintained harness:

```powershell
pwsh -NoProfile -File .\.agents\skills\verify-orelay\scripts\verify.ps1 -RunId manual-check
```

It proves the following user paths through the public executable:

1. `--version`, `--help`, invalid input, and `doctor --help` return the expected exit codes.
2. `init`, `config get`, `config set`, and `config clear` use a selected file. Clearing `port` removes the saved override and restores `12987`. Starting with `--port` uses the invocation value while preserving the saved value.
3. Two registrations are posted concurrently. Each receives a distinct ID. Two callback flows reach separate run-owned listeners, and the complete raw query survives the redirect, including encoded values, repeated fields, and an empty value.
4. The short lease expires one registration. The other stays alive after a renewal, then deletion is proved idempotent and stops routing.
5. Read-only doctor reports a healthy owned instance without changing its configuration and reports a missing selected file without creating it.
6. The SQLite registration file preserves live registrations, renewals, and deletions across killed relay processes. A lease that expires during downtime stays expired, and a second configuration uses an independent database.

For focused checks, use the repository scripts and the public test projects:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Configuration Release
pwsh -NoProfile -File .\scripts\test.ps1 -Configuration Release
pwsh -NoProfile -File .\scripts\format.ps1
```

The helper does not claim service-manager, Tailscale, or Aspire coverage. Use the feature map for their exact source files and commands.

For server logging changes, also run `.github/workflows/logging-smoke.ps1` against the published executable. The [relay feature map](features/relay-leases.md#console-logging) covers captured text/JSON output, redaction, and terminal colour checks.

For versioning and release changes, follow [versions and releases](features/versions-releases.md). Use its isolated Git fixture and package-consumer check before an authorized release tag is pushed.

For bootstrap, installation, and self-update changes, follow [installation and updates](features/installation-updates.md). Never use the user's installed binary, service, or configuration as the update target.

For automatic service-update changes, pass `-CheckAutoUpdate` to the helper for the native foreground suppression check. It adds 65 seconds while the saved opt-in is enabled. This is separate from the disposable service-manager proof described in that feature map.

For setup changes, run `.github/workflows/setup-smoke.ps1` against the published executable using a run-owned `-RunRoot`. See [CLI and saved configuration](features/cli-config.md) for defaults, unattended setup, cancellation, and preservation checks. Service setup is separately exercised by the disposable Windows/Linux service smoke; the local helper never installs a service.

## Evidence

Each run writes proof to `.artifacts/verification/<run-id>/` and temporary state to `work/verification/<run-id>/`. The evidence includes the executable inventory and SHA-256, command stdout and stderr with exit codes, health, configuration actions, doctor JSON, concurrent registration responses, exact callback locations and received targets, lease renewal and expiry, deregistration, and per-process cleanup records.

The evidence standard is the user path plus its observable result. A successful status code alone is not enough for callbacks. The helper checks the destination's raw HTTP request line. A healthy relay probe does not prove browser reachability, and no provider grant or token exchange is attempted. All OAuth values are synthetic and the relay's own output is retained only as captured by the run.

Aspire package verification uses the consuming AppHost and real DCP. The checked sample commands are in `samples/GUIDE.md`; the focused and integration test commands are recorded there. The existing package proof is retained under `.artifacts/verification/aspire-package/`. Run that path only when DCP and the required SDK are available.

Native platform coverage is per published RID. The checked-in native workflow runs `.github/workflows/native-smoke.ps1` on its matching runner. Local evidence from this skill covers the Windows executable passed to the helper. It does not prove Linux, macOS, Windows ARM64, service installation, code signing, or package publication.

## Cleanup

The helper stops only its recorded relay processes, closes only its callback listeners, disposes its HTTP clients, and removes its own `work/verification/<run-id>/` tree. It never deletes `.artifacts/verification/<run-id>/`, the publish directory, the installed service, or the user's default configuration. A failed run still writes `failure.json` and `cleanup.json` before it exits.

If a run is interrupted, inspect the latest evidence for the recorded process IDs and terminate only those owned IDs. Remove the matching scratch directory after confirming the processes have exited. Keep the evidence directory for the failure report.

## Helpers

The executable helper is `.agents/skills/verify-orelay/scripts/verify.ps1`. Its invocation is shown above. It accepts `-RuntimeIdentifier`, `-ExecutablePath`, and `-RunId`, returns a nonzero exit code on any failed assertion, and prints the evidence path on success. Run IDs are restricted to safe path characters, cannot reuse an existing evidence or scratch directory, and are cleaned only after the full resolved scratch path is checked as a child of `work/verification`.

Use `/maintain-verification-skill` when a user-facing command or callback contract changes. Read the feature map before editing it, then exercise every mapped path that the available platform and external services permit. Record blocked prerequisites instead of calling an unrun path passed.
