# 13: Install and operate the Linux systemd service from the same executable

Status: resolved

Blocked by: [04: Keep concurrent worktrees registered without orphans](04-concurrent-registration-leases.md).

Parent: [ORelay v1 specification](../spec.md#build-layout-and-distribution)

**What to build:** A Linux operator uses the native `orelay` executable to install, control, inspect, and remove its systemd service with explicit configuration and ownership.

## Acceptance criteria

- [x] Implement `service install`, `start`, `stop`, `restart`, `status`, and `uninstall` for systemd using the same native executable as foreground mode.
- [x] Record absolute executable and selected config paths with correct systemd argument escaping. Document the chosen service identity, unit scope, startup behavior, and required privileges.
- [x] The service account can read and, where required, initialize its selected config. Invalid paths, missing prerequisites, and permission failures report actionable errors without silently selecting another location.
- [x] Start reaches observable readiness and routes callbacks. Graceful termination and stop release the owned listener and background work. Restart exposes the documented loss of in-memory registrations.
- [x] Repeated lifecycle commands have documented outcomes. A conflicting unit is reported rather than overwritten without ownership checks; status reflects the actual service manager state.
- [x] Add read-only systemd doctor checks, consistent structured results, documented exit codes, and clear unsupported-environment errors when systemd is unavailable.
- [x] Uninstall removes only owned service integration, preserving saved configuration and the executable. Leave unrelated units and processes untouched.
- [x] Document operator commands and troubleshooting, and add focused tests plus a live service-manager verification recipe with explicit platform and privilege prerequisites.

## Verification

On an owned disposable Linux environment with systemd, run the full native install/start/callback/status/stop/restart/uninstall path. Exercise alternate config paths, path escaping, insufficient privileges, repeated operations, and a conflicting unit. Confirm config persistence and complete owned-resource cleanup, including after failure. A container lacking systemd is not evidence that the service-manager path passed.

## Scope

This ticket covers systemd. It does not add other init systems, container deployment, macOS launchd, or changes to the user's existing services.

## Delivery

Completed and reviewed on 2026-09-22. The published Linux executable passed real systemd install/start/status/restart/stop/uninstall, exact callback delivery, conflict refusal, repeated commands, paths with spaces and dollar signs, registration loss, and config preservation. An ordinary unprivileged account proved PermissionDenied with exit 3. Final CI evidence records owned unit removal.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
