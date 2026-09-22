# 12: Install and operate the Windows service from the same executable

Status: resolved

Blocked by: [04: Keep concurrent worktrees registered without orphans](04-concurrent-registration-leases.md).

Parent: [ORelay v1 specification](../spec.md#build-layout-and-distribution)

**What to build:** A Windows operator uses the native `orelay` executable to install, control, inspect, and remove its Windows Service while preserving saved configuration.

## Acceptance criteria

- [x] Implement `service install`, `start`, `stop`, `restart`, `status`, and `uninstall` against the Windows service manager, using the same native executable as foreground mode.
- [x] Installation records absolute executable and selected config paths, with correct handling of spaces and quoting. The service uses that configuration regardless of its working directory.
- [x] Document the service identity, required privileges, startup behavior, and config access. A missing or unwritable config and insufficient privileges produce actionable failures rather than silent path changes or incomplete success.
- [x] Normal start reaches observable readiness and routes callbacks. Stop follows the host shutdown path. Restart has the documented in-memory registration-loss behavior, without claiming pending OAuth flows survive it.
- [x] Repeated lifecycle commands have documented safe outcomes. Existing or conflicting service definitions are identified rather than overwritten without ownership checks.
- [x] Status accurately distinguishes installed/running/stopped/failing states. Add read-only service doctor checks and JSON/exit behavior consistent with the existing CLI.
- [x] Uninstall removes only owned service integration, stops the owned service as needed, and preserves its saved configuration. It does not delete the executable or interfere with unrelated services.
- [x] Document manual use and service-account troubleshooting for humans, and add focused service-command tests plus a real Windows verification recipe with explicit privilege requirements.

## Verification

On an owned disposable Windows environment, use the published Native AOT executable to install, start, query, stop, restart, and uninstall the service. Route a synthetic callback while it is running. Exercise a path with spaces, alternate config location, insufficient privileges, repeated commands, and a conflicting service definition. Confirm config survives and no run-owned service or process remains; retain evidence outside cleanup targets.

## Scope

Use test-owned service names or an isolated disposable machine for verification. Do not install or replace James's real service during routine testing. Linux and macOS service behavior are not implemented here.

## Delivery

Completed and reviewed on 2026-09-22. The published Windows executable passed real SCM install/start/status/restart/stop/uninstall, exact callback delivery, conflict refusal, repeated commands, path escaping, registration loss, and config preservation. A temporary non-admin account proved PermissionDenied with exit 3 and no service creation. Final CI evidence includes service and account removal.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
