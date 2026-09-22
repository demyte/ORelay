# 11: Diagnose problems and apply targeted CLI repairs

Status: ready-for-agent

Blocked by: [09: Run a shared relay with explicit network configuration](09-shared-relay-networking.md), [10: Discover worktree callback addresses with explicit overrides](10-callback-address-discovery.md).

Parent: [ORelay v1 specification](../spec.md#cli-and-configuration)

**What to build:** An operator or agent can run `doctor` to understand a broken setup and use `doctor --fix` for documented, bounded repairs with accurate results and exit codes.

## Acceptance criteria

- [ ] Extend the existing config doctor to check the selected config, intended listener, advertised URL, discovery prerequisites, management connectivity, and applicable registration prerequisites. Identify the target and network position of each check.
- [ ] Default doctor mode is read-only. It does not create a config, start or stop a service, change bindings, register a new worktree, or initiate provider authorization just to inspect health.
- [ ] `doctor --fix` creates missing configuration from the selected defaults/overrides and performs only documented repairs. Each result distinguishes what was detected, changed, left unchanged, or still failing.
- [ ] Repeating supported fixes is harmless. Malformed configuration is reported and preserved rather than replaced. Permission errors identify the selected path and do not trigger fallback storage.
- [ ] Occupied ports, unreachable management endpoints, denied access, invalid advertised addresses, unavailable discovery, and incompatible application bindings produce concrete next steps. Do not report a successful relay-side probe as proof of browser reachability.
- [ ] Installing a service, expanding network access, changing firewall rules, or provisioning credentials remains an explicit operation rather than an implicit repair.
- [ ] Integrate platform service diagnostics as those implementations become available. Preserve any checks already added by tickets 12 or 13; do not require either service to exist for ordinary foreground diagnostics.
- [ ] Provide help, examples, stable JSON output, separated diagnostic output, and documented nonzero exit behavior when failures remain after repair. Extend the verification map with both read-only and repair paths.

## Verification

Run against owned healthy and deliberately broken configurations and instances. Compare files, registration state, and service state before and after read-only checks. Prove missing-file repair, repeated repair, malformed-file preservation, occupied-port reporting, and partial failure through actual CLI invocations. Validate machine-readable results and exit codes, run focused checks, and retain sanitized evidence after cleanup.

## Scope

Doctor diagnoses ORelay's current capabilities. It does not need a live Xero grant, an external package publisher, or administrator privileges for unrelated checks. Platform-specific service behavior stays in its platform ticket.
