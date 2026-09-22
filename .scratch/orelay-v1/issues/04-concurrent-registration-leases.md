# 04: Keep concurrent worktrees registered without orphans

Status: resolved

Blocked by: [03: Route a delegated callback through a local relay](03-local-callback-relay.md).

Parent: [ORelay v1 specification](../spec.md#callback-and-registration-behavior)

**What to build:** Multiple active worktrees can retain independent registrations, renew them while running, remove them on normal shutdown, and rely on expiry after a crash.

## Acceptance criteria

- [x] Add owned lease renewal with documented timing, expiry boundary, retry guidance, and allowed bounds. Use the proposed five-minute lifetime and one-minute renewal interval as initial defaults unless implementation evidence requires a documented adjustment.
- [x] Renewal and deletion use the registration ID without credentials, per decision 08. Document that reachable clients can manage registrations; do not claim ownership isolation or treat IDs as authentication.
- [x] Expired entries stop routing immediately at the defined boundary and are removed from memory without requiring another callback. Late renewal cannot resurrect an expired entry.
- [x] Graceful deregistration is idempotent. Renewal, callback, expiry, and deletion races have defined outcomes and cannot resurrect or retarget removed entries.
- [x] Multiple registrations and overlapping flows remain isolated. Document capacity limits and capacity-exceeded behavior without hardcoding the service to a fixed number of worktrees.
- [x] A stopped or crashed owner cannot keep an entry alive indefinitely. Reusing a worktree port with a new registration does not make an old expired or deleted ID valid again. Document that a still-live lease can outlast a crashed process until expiry.
- [x] A relay restart loses the registry. Old IDs fail, and new sessions receive new IDs. Do not claim pending callbacks survive that restart.
- [x] Document the lifecycle and extend focused tests plus the live verification map. Keep timing tests deterministic where possible, while proving at least one real process-crash cleanup path.

## Verification

Run at least three worktree listeners with overlapping synthetic flows. Renew one registration while another expires; delete a third and confirm the others still work. Crash an owner, wait through the declared expiry, and prove its ID no longer routes. Reuse a destination port with a new registration and test the old ID. Use a controllable clock in focused boundary/race tests; retain live evidence after cleanup.

## Scope

No database or persisted registration store. Aspire owns renewal only when ticket 06 is implemented. This ticket does not add application restart automation.

## Delivery

Completed and reviewed on 2026-09-22. In-memory atomic leases, background expiry, idempotent deletion, capacity, and concurrent routes are implemented. Three simultaneous destination listeners proved renew/expire/delete isolation; an external AppHost crash proved expiry and fresh IDs after port reuse. Evidence: `root-three-abe2e87c` and `aspire-crash`.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
