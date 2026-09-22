# 06: Register a real Aspire application using the hosting NuGet package

Status: resolved

Blocked by: [04: Keep concurrent worktrees registered without orphans](04-concurrent-registration-leases.md), [05: Decide how Aspire exposes registration loss and recovery](05-aspire-recovery-decision.md).

Parent: [ORelay v1 specification](../spec.md#aspire-package-and-recovery)

**What to build:** A consuming AppHost references `ORelay.Aspire.Hosting`, connects to an independently running relay, and manages the normal registration lifecycle of its application's allocated callback endpoint.

## Acceptance criteria

- [x] Pack `ORelay.Aspire.Hosting` as a separate NuGet library and consume the produced package from a local feed in a sample AppHost. A project reference alone is insufficient packaging evidence.
- [x] Resolve a resource's actual allocated endpoint and callback path, register before the application starts, and provide the registration ID plus fixed relay callback URI using the contract chosen in 05. Document the hosting extensions and required options.
- [x] Pin the Aspire dependency centrally and use its supported lifecycle APIs. Prove endpoint allocation, registration, and application-start ordering rather than relying on timing sleeps.
- [x] Run renewal in the AppHost so pausing the application itself does not expire an otherwise healthy registration. Failed initial registration produces an actionable resource/startup failure rather than starting with missing or invalid relay data.
- [x] Deregister when the registered resource or AppHost stops normally. Cancel owned renewal work and keep the shared relay running.
- [x] Two consuming AppHosts with different application ports hold separate registrations and can complete overlapping delegated flows. Stopping one does not remove the other's registration.
- [x] The consumer example constructs and validates complete state and performs code exchange directly against a synthetic provider using the fixed relay redirect URI. Explain the application changes required; the hosting package cannot rewrite arbitrary existing OAuth code automatically.
- [x] Document package consumption, local callback selection, and the distinction between normal lifecycle behavior and the recovery work in 07. Extend the verification map and focused integration tests.

## Verification

Pack and restore the real package into the sample, start an owned relay and two AppHosts, and drive their public authorization paths. Capture the allocated ports, registration/start ordering, callback destination, and resulting validated flow. Pause one API while observing continued AppHost renewal, then stop one AppHost and verify the other remains usable. Retain sanitized evidence and clean up all run-owned resources.

## Scope

Implement the normal lifecycle and selected public contract. Ticket 07 adds restart and outage recovery. Do not publish NuGet packages externally or let an AppHost own the lifetime of the shared relay.

## Delivery

Completed and reviewed on 2026-09-22. The independent hosting NuGet package is consumed by real AppHosts from the local feed. Packed version `0.1.0-audit.20260922191012` passed all 14 hosting tests without skips, including overlapping delegated flows and renewal while an API is suspended. Evidence: `root-aspire-fe7a88d7` and `aspire-crash`. Package consumption uses the repository build configuration.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
