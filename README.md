# ORelay

ORelay is a planned small, cross-platform local-development OAuth 2 authorization callback relay. It is intended for delegated API access, such as a local tool using Xero, rather than application sign-in.

The design uses one provider-registered callback URL for a machine. Multiple Git worktrees can listen on different local ports while sharing that callback. Each worktree would register its callback URL and receive an opaque registration ID. Each OAuth request would put that ID and its own opaque per-request value in `state`. ORelay would route the complete provider callback query to the matching worktree, preserving `state`, `code`, error fields, and other query values unchanged.

The worktree would validate the complete state value and own the authorization-code exchange, access tokens, and refresh tokens. Both the authorization request and token exchange would use the fixed relay `redirect_uri`. ORelay only routes callbacks. The provider remains responsible for grant, consent, user, and tenant semantics, so a callback registration does not promise an independent provider grant for every worktree.

## Status

This repository is an initial scaffold and design proposal. There is no relay implementation, runnable command, service installer, hosting package, or supported configuration contract yet. Names and paths below are proposed and may change as implementation work is authorized.

## Planned behavior

- The relay would listen locally by default and accept callback destinations on loopback interfaces only.
- Registrations would live in memory. ORelay would store no database records, authorization codes, access tokens, or refresh tokens.
- A registration lease would limit the lifetime of a crashed worktree registration. The sample proposal renews a lease every minute and expires it after five minutes.
- Graceful deregistration would be best effort. Expired or unknown registration IDs would be rejected, with no fallback destination. New registrations would receive IDs distinct from old sessions.
- Relay restart and registration-ID propagation remain design work. A running API must have a deliberate mechanism for receiving a replacement ID; this proposal does not claim transparent recovery.

## Proposed implementation

The proposed stack is .NET 10 with ASP.NET Core Minimal API and Kestrel. Packaging may produce self-contained executables for `win-x64`, `linux-x64`, and `linux-arm64`. Direct console operation is the first proposed mode. Native Windows Service support, PowerShell install and uninstall scripts, and a Linux systemd unit and scripts are possible later additions. Native AOT is optional future work.

The proposed source layout is:

```text
src/
  ORelay/
  OAuthRelay.Aspire.Hosting/
packaging/
  windows/
  linux/
```

`OAuthRelay.Aspire.Hosting` would remain a separate planned package. Its proposed `WithOAuthRelay(...)` extension would resolve the relay endpoint, start registration, inject configuration before an API starts, renew the AppHost lease, and clean up on shutdown or resource restart. The restart and reconnection contract still needs a design decision.

## Planned local flow

1. Start one relay for the machine.
2. A worktree registers its loopback callback URL and receives an opaque registration ID.
3. The worktree creates a provider authorization request using the fixed relay callback and a state value containing the registration ID and its own per-request value.
4. The provider redirects to the relay. The relay validates the registration lease and forwards the complete callback query to that worktree.
5. The worktree validates state and exchanges the code with the provider using the same fixed relay callback. The worktree keeps its tokens and refresh logic.

These steps describe the proposal. They are not commands or supported features in this initial repository.

## Contributing

Keep changes within the callback-routing relay scope. Keep Aspire integration and packaging separate from the relay core. Add focused tests for routing, concurrent registrations and callbacks, and lease expiry before calling those behaviors complete. Never log callback query strings, authorization codes, access tokens, refresh tokens, or secrets. Update this document when the proposal becomes an implemented contract, and make build, run, and test instructions match commands that exist in the repository.
