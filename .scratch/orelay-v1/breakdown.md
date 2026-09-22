# Proposed ORelay v1 ticket breakdown

Status: needs-triage

This is the breakdown for review before publishing individual implementation tickets. The [specification](spec.md) preserves the agreed requirements and marks open decisions. Ticket numbers below are local to this effort. Dependencies name only work that genuinely gates the slice, not every earlier ticket.

Every implementation ticket will include observable acceptance criteria, focused feedback commands, documentation for its new behavior, and a maintained verification-skill entry. No ticket is ready for unattended implementation merely because it appears in this draft.

## Proposed slices

1. **01: Run the native CLI and its agent feedback loop**
   - Blocked by: None.
   - Delivers: A runnable `orelay --help` and `--version`, basic parser errors and exit codes, and a host-native AOT executable. Establish the .NET 10 solution, required shared build files, MIT license, and purposeful separation between the executable and future Aspire package.
   - Proof: Invoke the published executable outside the repo, confirm help/version have no configuration side effects, run format/lint/build checks, and execute a real initial verification skill with retained evidence. Add the first CI feedback job. Do not invent help for unimplemented commands.

2. **02: Initialize, inspect, and edit persistent configuration**
   - Blocked by: 01.
   - Delivers: `init`, `config get/set/clear`, global `--config-file`, schema validation, default resolution, atomic writes, and a read-only config doctor. The executable directory is the default config location even when the working directory changes.
   - Proof: Drive missing/existing/malformed files, transient versus saved settings, clear-to-default, unknown keys, concurrent writers, unwritable locations, and idempotent init. Verify actionable text and JSON results. Preserve config and evidence appropriately during cleanup.

3. **03: Route a delegated callback through a local relay**
   - Blocked by: 02.
   - Delivers: Foreground `server`, first-run config creation, an in-memory registry, registration and deregistration API, and the fixed GET callback. Demonstrate a complete synthetic authorization flow between provider stub, relay, and worktree. Registrations have bounded lifetime from the start.
   - Proof: Follow the browser redirect and validate full state at the worktree. Check raw query preservation, provider errors, malformed or duplicate state, unknown destinations, expired registrations, distinct IDs, and absence of sensitive values in logs. Prove startup overrides are written only when the config is missing.
   - Boundary: Loopback operation only at this increment. Define separate per-registration management authority so the browser-visible routing ID alone cannot renew, delete, or retarget an entry. Broader shared access waits for 08 and 09.

4. **04: Keep concurrent worktrees registered without orphans**
   - Blocked by: 03.
   - Delivers: Owned lease renewal, expiry cleanup, idempotent deregistration, documented timing and capacity behavior, and isolated registrations for many simultaneous worktrees. A crashed owner eventually disappears without relying on shutdown callbacks.
   - Proof: Run several callback listeners and overlapping flows, renew one while another expires, deregister one while others remain active, and exercise renewal/expiry races with a controllable clock in focused tests. A live run proves crash cleanup, route isolation, and no resurrection of expired entries.

5. **05: Decide how Aspire exposes registration loss and recovery**
   - Blocked by: None.
   - Delivers: A reviewed contract for initial injection, relay restart, lease loss, resource restart, and replacement registration IDs. Decide whether v1 explicitly requires restarting the affected application or supplies a dynamic source that the application can consume.
   - Proof: Walk through startup, transient disconnect, expiry, relay restart during authorization, changed ports, and AppHost shutdown. Identify the consumer integration needed for full state validation and the fixed redirect URI.
   - Type: Design decision requiring James's input. This can run alongside 01 through 04; it does not require implementing a second client package or restarting applications to explore the decision.

6. **06: Register a real Aspire application using the hosting NuGet package**
   - Blocked by: 04, 05.
   - Delivers: `ORelay.Aspire.Hosting` consumed by a sample AppHost from a locally packed NuGet package. Register an allocated callback before application startup, expose the required values, renew from the AppHost, and deregister on normal stop. Connect to an independently running relay.
   - Proof: Start two AppHosts with different ports, complete synthetic flows, pause one API while its AppHost renews, and stop one without disturbing the other or the relay. Verify lifecycle order and demonstrate the worktree's own state construction and validation.

7. **07: Recover predictably from Aspire and relay restarts**
   - Blocked by: 06.
   - Delivers: The recovery contract selected in 05, including visible degraded state, bounded retry behavior, expiry handling, resource restart, and replacement endpoint registration. Pending old flows fail as documented rather than reaching a different session.
   - Proof: Interrupt the relay, recover it, expire a lease, restart a resource on another port, and verify the application either receives valid replacement data through the chosen mechanism or reports the required restart. Show that stopped resources are not re-registered by a late retry.

8. **08: Decide management access and destination rules for a shared relay**
   - Blocked by: None.
   - Delivers: A reviewed policy for client identity, registration ownership, allowed callback destinations, secrets, management versus callback access, and HTTPS or trusted network/proxy topology. Preserve the loopback default while supporting central agents and Tailscale.
   - Proof: Walk through two clients attempting to read or mutate each other's registrations, an anonymous browser callback, arbitrary redirect destinations, malformed Host headers, and the chosen deployment topology. State which boundary enforces each rule.
   - Type: Design decision requiring James's input. It gates shared exposure only; local CLI, configuration, callback work, and the Aspire recovery decision can proceed independently.

9. **09: Run a shared relay with explicit network configuration**
   - Blocked by: 04, 08.
   - Delivers: Non-loopback `--bind`, explicit advertised relay callback URL, and the management and destination policies selected in 08. Authorized clients can register multiple worktrees across machines without conflating listener addresses with browser URLs.
   - Proof: Exercise allowed and denied clients, ownership isolation, allowed and denied destinations, incorrect advertised addresses, and a callback in the selected protected topology. Verify configuration persistence, diagnostics, JSON errors, and the documented startup requirements.

10. **10: Discover worktree callback addresses with explicit overrides**
    - Blocked by: 06, 09.
    - Delivers: Local Aspire endpoint discovery, optional Tailscale discovery on the worktree host, and explicit URL/hostname overrides. Shared-server settings stay separate from per-worktree endpoint choices. Discovery produces the exact URL registered with the relay.
    - Proof: Exercise a local endpoint, a Tailscale endpoint, no Tailscale installation, an ambiguous interface choice, a loopback-only application listener, and a usable explicit override. Demonstrate browser navigation to a remote worktree; report probes from other network positions honestly.

11. **11: Diagnose problems and apply targeted CLI repairs**
    - Blocked by: 09, 10.
    - Delivers: Full `doctor` and `doctor --fix` support for current config, listener, advertised URL, discovery, management connectivity, and registration prerequisites. Keep fixes explicit and limited. Establish a service-check extension point that the platform tickets complete.
    - Proof: Run doctor against owned healthy and deliberately broken instances; show read-only commands make no changes. Drive missing-config repair twice, malformed-config refusal, occupied-port reporting, and partial failures with accurate JSON and exit status. Do not start real provider flows to diagnose a relay.

12. **12: Install and operate the Windows service from the same executable**
    - Blocked by: 04.
    - Delivers: Windows `service install/start/stop/restart/status/uninstall`, using the same native executable and explicit absolute config path. Add service-specific doctor checks and clear privilege, account, path, and port errors.
    - Proof: On a disposable Windows environment, install and operate an owned service, route a callback, restart it, inspect status, and uninstall. Exercise paths with spaces, an alternate config location, repeated commands, and missing privilege. Confirm config survives uninstall and unrelated services are untouched.

13. **13: Install and operate the Linux systemd service from the same executable**
    - Blocked by: 04.
    - Delivers: Linux equivalents of the Windows service commands with owned systemd integration, explicit service identity, writable config location, signal handling, and service-specific doctor checks.
    - Proof: On a disposable Linux environment with systemd, run the full install/callback/restart/uninstall path, including missing privileges, path quoting, persistence, and process cleanup. A container without systemd does not count as service-manager verification.

14. **14: Produce and exercise native artifacts on Windows, macOS, and Linux**
    - Blocked by: 03.
    - Delivers: A native build and execution matrix, packaged executable assets with checksums and optional symbols, and clear OS prerequisites. Evaluate x64 and ARM64 coverage per OS and record the supported release matrix. Keep checks running as later features land.
    - Proof: Build on each matching OS, run the native artifact without an installed .NET runtime, initialize an isolated config, and complete a callback. Prove the executable does not need adjacent managed runtime files. Separately record any unexercised architecture instead of claiming support from compilation alone.
    - Boundary: Produce reviewable artifacts. Uploading a public release, publishing a package feed, code signing, notarization, and macOS service installation are not implied by this ticket.

15. **15: Verify the complete v1 workflow and prepare the release handoff**
    - Blocked by: 07, 11, 12, 13, 14.
    - Delivers: A full source-and-live audit of the maintained verification map, a checked NuGet consumer walkthrough, human setup and troubleshooting documentation, and a release-readiness record. This reconciles existing feature evidence rather than deferring all testing until the end.
    - Proof: Use the verification skill to cover every implemented feature and each claimed platform, including multiple active worktrees, callback errors, cleanup after crashes, selected shared topology, config repairs, service lifecycle, and native binaries. Keep evidence and list any unresolved prerequisites or defects explicitly.
    - Boundary: Do not hide product defects by rewriting expected verification behavior. Product corrections require scoped follow-up work; package publication remains separate.

## Review points

- Are these slices small enough to implement and verify in one fresh agent context? The first native CLI slice includes developer feedback intentionally.
- Do 05 and 08 capture the two product decisions still needed, without blocking unrelated local work?
- Do the dependencies reflect real gates? In particular, platform services can proceed independently of Aspire and shared networking, and native build automation can start before all features exist.
- Should any implementation slices be merged or split? After review, publish each approved ticket to its own numbered file under this effort's `issues/` directory with acceptance criteria and a readiness status.
