# 09: Run a shared relay with explicit network configuration

Status: resolved

Blocked by: [04: Keep concurrent worktrees registered without orphans](04-concurrent-registration-leases.md), [08: Decide management access and destination rules for a shared relay](08-shared-relay-access-decision.md).

Parent: [ORelay v1 specification](../spec.md#networking-and-discovery)

**What to build:** An operator can deliberately bind ORelay to a shared interface, advertise its fixed callback URL, and allow clients on other machines to register destinations without authentication in this version, as James selected in 08.

## Acceptance criteria

- [x] Support explicit non-loopback binding, including wildcard or Tailscale addresses, while retaining the loopback default. Validate the selected bind and port and report failures clearly.
- [x] Configure the advertised relay callback URL separately from the listener. Reject unusable wildcard advertised hosts and do not derive a trusted callback URL from arbitrary request Host or source-address values.
- [x] Apply decision 08: no API authentication or management credentials; default loopback, deliberate non-loopback binding, strict URL validation, and no mandatory allowlist or TLS deployment gate.
- [x] Multiple reachable clients can register and route distinct worktree flows concurrently. Document that management operations are unauthenticated. Do not add credential checks or claim cross-client management isolation.
- [x] Validate destination URLs against the approved rules and preserve the full callback query when redirecting to an allowed destination. The relay still does not fetch destinations or exchange tokens.
- [x] Persist the new supported settings through the existing config commands and honor invocation precedence. Secret-bearing configuration and diagnostics follow the selected policy, including redaction in ordinary output.
- [x] Document exact startup prerequisites and a shared-deployment example using the chosen topology. Do not claim a hostname alone proves protected transport or browser reachability.
- [x] Extend help, structured errors, focused access-policy tests, and the live verification map. Keep loopback-only use functional without shared-network dependencies.

## Verification

In an owned shared test deployment, use multiple clients to register and route callbacks without credentials. Exercise URL rejection, wrong advertised settings, wildcard binding, and untrusted-header handling. Demonstrate navigation through the shared relay to the intended worktree. Capture sanitized evidence, state the network positions tested, and remove only run-owned resources.

## Scope

Follow 08's agreed policy rather than choosing a new credential or deployment model here. Endpoint discovery comes in 10. Automatic firewall changes, Tailscale installation, certificate provisioning, and provider configuration changes are excluded.

## Delivery

Completed and reviewed on 2026-09-22. Explicit shared binding and advertised URLs are implemented with strict destination validation and no management authentication, as approved in 08. Two concurrent synthetic browser clients on a separate Tailscale host completed exact callbacks through the shared Windows relay. Evidence: `root-remote-042b7b4f`, plus focused HTTP policy tests.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
