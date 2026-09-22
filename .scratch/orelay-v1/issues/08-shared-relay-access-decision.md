# 08: Decide management access and destination rules for a shared relay

Status: resolved

Blocked by: None.

Parent: [ORelay v1 specification](../spec.md#decisions-still-needed)

**What to build:** A recorded, approved access and networking policy that lets many agents use a central relay without treating browser-visible registration IDs as management credentials or accepting arbitrary redirect destinations.

## Resolution

James deferred authentication on 2026-09-22: "no auth required for shared relay as yet - worry about it later." The decision below supersedes the proposed authenticated-client policy and closes this decision ticket.

## Decision

This version has no registration API authentication, API keys, per-client identities, or per-registration management credentials. Clients that can reach the management API can register destinations, renew leases, and delete registrations. Browser callbacks need only a valid live routing ID inside state. Document this behavior honestly; do not imply random IDs establish ownership or confidentiality.

Default to loopback. Non-loopback binding is an explicit operator choice and permits remote callback destinations. Validate exact HTTP/HTTPS URLs with no credentials, fragments, or existing query, preserve callback queries, reject malformed input, and avoid sensitive logs. Do not gate shared operation on a new allowlist, certificate installer, or mandatory TLS/Tailscale arrangement. A selected external network or TLS proxy can provide protection independently; do not infer it from a hostname or untrusted headers.

Acceptance evidence: James's quoted instruction. Authentication and authorization hardening are deferred, not implementation requirements or blockers for ticket 09. Its relevant runtime proof is configuration, URL validation, routing, and concurrent-client behavior.

## Acceptance criteria

- [x] Record James's explicit deferral of authentication for shared mode.
- [x] Define unauthenticated management and browser callbacks without ownership guarantees.
- [x] Keep loopback default, explicit shared binding, exact HTTP/HTTPS destination validation, and advertised URL independent of untrusted headers.
- [x] Remove the superseded credential/isolation requirements from tickets 03, 04, and 09 and record the override in the specification and AGENTS.
- [x] Leave authentication, allowlists, and managed TLS provisioning outside this version's required implementation.

## Verification

The recorded user instruction resolves the policy choice. Ticket 09 verifies credential-free shared registration and routing, malformed destination rejection, explicit binding, and correct advertised addresses. Do not claim management isolation or protected transport from that proof.

## Scope

Decision work only. This ticket does not install Tailscale, provision certificates, expose a listener, add a hosted service, or implement provider authorization.
