# 07: Recover predictably from Aspire and relay restarts

Status: ready-for-agent

Blocked by: [06: Register a real Aspire application using the hosting NuGet package](06-aspire-hosting-package.md).

Parent: [ORelay v1 specification](../spec.md#aspire-package-and-recovery)

**What to build:** A consuming AppHost and application show the agreed degraded and recovery behavior when their registration disappears or their resource restarts, without accepting callbacks for an obsolete session.

## Acceptance criteria

- [ ] Implement the recorded decision in ticket 05. If it requires a restart, expose that requirement and an actionable recovery step. If it uses dynamic registration data, prove the running application consumes the new ID through that mechanism.
- [ ] Distinguish temporary connection failures from an unknown or expired registration. Apply the agreed bounded retries and cancellation; failures must not create uncontrolled repeated registrations.
- [ ] Relay restart or lease loss produces the documented visible degraded state. Recovery cannot silently leave the application constructing new flows with an obsolete ID.
- [ ] Resource restart registers its current allocated destination with a new session identity. Old callbacks cannot be routed by retargeting the previous registration to the new destination.
- [ ] Pending old flows fail as documented. The worktree's full-state validation remains in force, and recovery does not imply preservation of in-flight flows across relay registry loss.
- [ ] A resource stop or AppHost shutdown cancels renewal and recovery. Late responses and retries cannot resurrect its registration or affect another AppHost's entry.
- [ ] Update package documentation, the sample, focused lifecycle tests, and live verification recipes to reflect the behavior actually proved. Keep the executable's independent lifecycle intact.

## Verification

Drive an authorization flow while interrupting and restoring the owned relay. Separately let a lease expire, restart a resource on another port, and stop a resource while recovery is in progress. Observe application-visible state, current registration identity, obsolete-flow failure, and subsequent new-flow success after the selected recovery action. Include two active AppHosts to prove isolation. Capture sanitized evidence and verify cleanup.

## Scope

The chosen contract governs this ticket. Do not add a persistent registry, an unapproved runtime package, or automatic application restarts to make recovery appear seamless.
