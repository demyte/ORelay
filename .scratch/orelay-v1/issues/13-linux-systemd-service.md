# 13: Install and operate the Linux systemd service from the same executable

Status: ready-for-agent

Blocked by: [04: Keep concurrent worktrees registered without orphans](04-concurrent-registration-leases.md).

Parent: [ORelay v1 specification](../spec.md#build-layout-and-distribution)

**What to build:** A Linux operator uses the native `orelay` executable to install, control, inspect, and remove its systemd service with explicit configuration and ownership.

## Acceptance criteria

- [ ] Implement `service install`, `start`, `stop`, `restart`, `status`, and `uninstall` for systemd using the same native executable as foreground mode.
- [ ] Record absolute executable and selected config paths with correct systemd argument escaping. Document the chosen service identity, unit scope, startup behavior, and required privileges.
- [ ] The service account can read and, where required, initialize its selected config. Invalid paths, missing prerequisites, and permission failures report actionable errors without silently selecting another location.
- [ ] Start reaches observable readiness and routes callbacks. Graceful termination and stop release the owned listener and background work. Restart exposes the documented loss of in-memory registrations.
- [ ] Repeated lifecycle commands have documented outcomes. A conflicting unit is reported rather than overwritten without ownership checks; status reflects the actual service manager state.
- [ ] Add read-only systemd doctor checks, consistent structured results, documented exit codes, and clear unsupported-environment errors when systemd is unavailable.
- [ ] Uninstall removes only owned service integration, preserving saved configuration and the executable. Leave unrelated units and processes untouched.
- [ ] Document operator commands and troubleshooting, and add focused tests plus a live service-manager verification recipe with explicit platform and privilege prerequisites.

## Verification

On an owned disposable Linux environment with systemd, run the full native install/start/callback/status/stop/restart/uninstall path. Exercise alternate config paths, path escaping, insufficient privileges, repeated operations, and a conflicting unit. Confirm config persistence and complete owned-resource cleanup, including after failure. A container lacking systemd is not evidence that the service-manager path passed.

## Scope

This ticket covers systemd. It does not add other init systems, container deployment, macOS launchd, or changes to the user's existing services.
