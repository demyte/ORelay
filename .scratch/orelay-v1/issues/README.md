# ORelay v1 tickets

James approved the [15-item breakdown](../breakdown.md) on 2026-09-22. The tickets below publish that plan to the repository's local Markdown tracker, one file per ticket. The [specification](../spec.md) and breakdown are preserved as source documents, including their original draft labels; this index records the subsequent approval and publication.

James authorized implementation of all tickets with parallel subagents, meaningful verification, and reviewed incremental commits to main on 2026-09-22. Decision 05 selects explicit restart-required recovery. Decision 08 defers authentication, including for shared mode. Package and public release publication remain separate actions.

## Delivery and dependencies

`Status` records triage readiness until delivery; `resolved` records completed implementation and verification. `Blocked by` records prerequisites separately. A `ready-for-agent` ticket can be picked up only when its blockers have been delivered and verified. For decision blockers, completion includes James's recorded decision. If a decision changes downstream scope, revise the affected ticket before implementation.

Ticket 01 is the first implementation increment. Decisions 05 and 08 are resolved in their ticket bodies and supersede earlier design proposals. Ticket 14 records the supported architecture and minimum OS matrix from native execution evidence. The orchestrator records implementation progress and evidence in each ticket before marking it resolved.

| Ticket | Outcome | Status | Blocked by |
| --- | --- | --- | --- |
| [01](01-native-cli-and-feedback.md) | Native CLI and agent feedback loop | resolved | None |
| [02](02-persistent-configuration.md) | Persistent configuration and config doctor | resolved | 01 |
| [03](03-local-callback-relay.md) | Local delegated callback flow | resolved | 02 |
| [04](04-concurrent-registration-leases.md) | Concurrent worktree leases and cleanup | resolved | 03 |
| [05](05-aspire-recovery-decision.md) | Explicit restart-required recovery selected | resolved | None |
| [06](06-aspire-hosting-package.md) | Consuming AppHost and hosting NuGet package | resolved | 04, 05 |
| [07](07-aspire-restart-recovery.md) | Aspire and relay restart recovery | resolved | 06 |
| [08](08-shared-relay-access-decision.md) | Shared authentication deferred | resolved | None |
| [09](09-shared-relay-networking.md) | Shared relay networking and access | resolved | 04, 08 |
| [10](10-callback-address-discovery.md) | Callback discovery and explicit overrides | resolved | 06, 09 |
| [11](11-doctor-and-targeted-repairs.md) | Full doctor and targeted repairs | resolved | 09, 10 |
| [12](12-windows-service.md) | Windows Service lifecycle | resolved | 04 |
| [13](13-linux-systemd-service.md) | Linux systemd lifecycle | resolved | 04 |
| [14](14-native-platform-artifacts.md) | Verified native platform artifacts | resolved | 03 |
| [15](15-v1-verification-and-handoff.md) | Complete verification and release handoff | resolved | 07, 11, 12, 13, 14 |

## Evidence expected from implementation

Each implementation ticket includes acceptance criteria and a public-behavior verification recipe. Use the repository's feedback and evidence rules, keep the verification map current in the same change, and retain proof after cleanup. Record unmet platform or privilege prerequisites as unverified rather than successful.

Append later discussion or decisions to the relevant ticket under `## Comments`. Keep the detailed behavior in the linked specification rather than duplicating the entire specification in every ticket.

All 15 tickets are resolved. See the [verification record](../implementation-progress.md) and [release handoff](../../../docs/v1-handoff.md).
