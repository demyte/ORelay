# 03: Route a delegated callback through a local relay

Status: ready-for-agent

Blocked by: [02: Initialize, inspect, and edit persistent configuration](02-persistent-configuration.md).

Parent: [ORelay v1 specification](../spec.md#callback-and-registration-behavior)

**What to build:** A developer can start a loopback relay, register a worktree destination, and complete a synthetic delegated OAuth flow through one fixed provider callback while the worktree owns state validation and code exchange.

## Acceptance criteria

- [ ] `server` runs the Minimal API/Kestrel host from the same native executable. It uses the selected config and invocation overrides, defaults to loopback, and reports readiness and bind failures without sensitive request data.
- [ ] Before serving, a missing config is created from defaults plus supplied settings. Existing config is preserved when temporary overrides are used. A malformed or unwritable required config fails clearly.
- [ ] Document registration, deregistration, and GET callback contracts, including request/response fields and errors. A registration receives an opaque routing ID and bounded initial lifetime. Management operations have no authentication in this version, per decision 08; a registration cannot be retargeted.
- [ ] Store registrations only in memory. Validate exact loopback HTTP/HTTPS destinations; define the initial restrictions on credentials, fragments, and destination queries. A different destination or new session receives a new ID.
- [ ] Define and document the state-envelope grammar and bounds. Parse only the routing portion; the worktree portion stays opaque and can contain delimiters. The worktree records and validates the full state value.
- [ ] A valid callback redirects the browser to its registered destination with the original complete query, including state, code, errors, unknown fields, repeated non-state parameters, empty values, and original encoding. The relay neither fetches the destination nor exchanges the code.
- [ ] Missing, duplicate, malformed, unknown, deregistered, or expired routing state fails without a redirect to any fallback. Error bodies, logs, exceptions, and diagnostics do not reveal sensitive callback values or management credentials.
- [ ] A sample or behavioral test completes authorization using a synthetic provider, receives the forwarded response, and exchanges the code directly from the worktree using the fixed relay redirect URI. It demonstrates rejection of mismatched full state in the worktree.
- [ ] Add focused public-HTTP tests and live CLI/HTTP verification, preserve Native AOT compatibility, and document only the behavior now available.

## Verification

Launch an owned relay and worktree listener, register through the public API, and follow a synthetic success and denial callback. Compare the redirect's raw query with the input. Exercise encoding, repeated provider fields, malformed state, expired IDs, destination validation, and deregistration. Inspect sanitized output and confirm first-start versus existing-config persistence. Clean up only owned processes and files; retain proof.

## Scope

This increment accepts loopback operation only. Lease renewal and long-running concurrent ownership arrive in 04; shared exposure follows 08 and 09. Do not add token storage, provider-specific grant logic, or additional response modes.
