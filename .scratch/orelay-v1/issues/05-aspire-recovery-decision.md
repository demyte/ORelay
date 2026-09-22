# 05: Decide how Aspire exposes registration loss and recovery

Status: resolved

Blocked by: None.

Parent: [ORelay v1 specification](../spec.md#aspire-package-and-recovery)

**What to build:** A recorded, approved recovery contract that lets the Aspire package and consuming application handle missing or replacement registrations without pretending startup environment variables can change inside a running process.

## Resolution

James selected the recommended restart-required behavior on 2026-09-22. The contract below supersedes the proposal and closes this decision ticket.

## Decision

Register after endpoint allocation and before application start; inject the registration ID and fixed relay callback URI for that process. The AppHost owns renewal. Temporary connectivity failures retry within the lease lifetime. Unknown/expired registration or relay restart produces visible degraded/restart-required state; do not silently re-register while the application retains an old ID. An explicit affected-resource or AppHost restart registers its current endpoint with a fresh ID before the new process starts. Old in-flight flows fail and the user starts a new flow. Stopping a resource cancels renewal and recovery and best-effort deregisters without stopping the relay. There is no automatic application restart, persistent registry, or dynamic runtime package.

Acceptance evidence: James's message "#1 - recommended" and the contract above. Runtime proof belongs to tickets 06 and 07.

## Acceptance criteria

- [x] Record James's selection of manual restart-required recovery, with no runtime package or automatic restart.
- [x] Define initial injection, AppHost renewal ownership, application state validation, and fresh registration on explicit restart.
- [x] Distinguish short disconnect, lease loss, relay restart, resource restart, and shutdown outcomes in the contract.
- [x] State that pending old flows fail and stopped resources cannot re-register through a late retry.
- [x] Keep the observable runtime acceptance scenarios in tickets 06 and 07; decision completion is not a runtime verification claim.

## Verification

Walk through a scenario table from allocated endpoint to registration, application start, disconnect, recovery, and shutdown. Check that every transition identifies the owner, application-visible data, and pending-flow outcome. Completion evidence is the agreed contract and James's recorded decision, not an unexecuted claim of runtime recovery.

## Scope

Documentation and decision work only. This decision can proceed alongside the first local CLI and callback tickets; it does not depend on them being implemented.
