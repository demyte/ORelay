# ORelay

ORelay is a planned small, cross-platform local-development OAuth 2 authorization callback relay. It is intended for delegated API access, such as a local tool using Xero, rather than application sign-in.

The design uses one provider-registered callback URL for a relay instance. Multiple Git worktrees can listen on different ports or hosts while sharing that callback. Each worktree would register its callback URL and receive an opaque registration ID. Each OAuth request would put that ID and its own opaque per-request value in `state`. ORelay would route the complete provider callback query to the matching worktree, preserving `state`, `code`, error fields, and other query values unchanged.

The worktree would validate the complete state value and own the authorization-code exchange, access tokens, and refresh tokens. Both the authorization request and token exchange would use the fixed relay `redirect_uri`. ORelay only routes callbacks. The provider remains responsible for grant, consent, user, and tenant semantics, so a callback registration does not promise an independent provider grant for every worktree.

## Status

This repository is an initial scaffold and design proposal. There is no relay implementation, runnable command, service installer, hosting package, or supported configuration contract yet. Names and paths below are proposed and may change as implementation work is authorized.

The [v1 specification](.scratch/orelay-v1/spec.md) records the requirements and open decisions. The [implementation tickets](.scratch/orelay-v1/issues/README.md) contain the approved delivery breakdown and dependencies.

## Planned behavior

- The relay would listen on loopback by default, with explicit non-loopback binding and callback destinations for shared development environments, including Tailscale. Management authentication is deferred; reachable clients can manage registrations in this version.
- Registrations would live in memory. ORelay would store no database records, authorization codes, access tokens, or refresh tokens.
- A registration lease would limit the lifetime of a crashed worktree registration. The sample proposal renews a lease every minute and expires it after five minutes.
- Graceful deregistration would be best effort. Expired or unknown registration IDs would be rejected, with no fallback destination. New registrations would receive IDs distinct from old sessions.
- Registration loss would report a restart-required state. Explicitly restarting the affected application or AppHost would obtain a fresh ID; existing authorization flows would need starting again.

## Proposed implementation

The proposed stack is .NET 10 with ASP.NET Core Minimal API and Kestrel. Self-contained, single-file Native AOT executables are required for Windows, macOS, and Linux. The exact architecture and minimum OS matrix will be documented with native execution evidence. The same executable would run directly or provide Windows Service and Linux systemd installation and lifecycle commands. The project will use the MIT license.

The planned CLI includes `server`, `init`, `config get/set/clear`, `doctor`, `doctor --fix`, and `service` commands. Configuration would live in `orelay.json` beside the executable by default, with a global `--config-file` override. The server would create a missing file from defaults and supplied settings. When a file already exists, command-line settings would override it only for that invocation; permanent changes would use the config commands.

The proposed source layout is:

```text
src/
  ORelay/
  ORelay.Aspire.Hosting/
packaging/
  windows/
  linux/
```

`ORelay.Aspire.Hosting` would be a separate NuGet package referenced by a consuming AppHost. Its proposed `WithORelay(...)` extension would resolve the relay endpoint, start registration, inject configuration before an API starts, renew from the AppHost, and clean up on shutdown or resource restart. Registration loss would require an explicit application or AppHost restart.

## Planned local flow

1. Start one relay locally or connect to an explicitly configured shared relay.
2. A worktree registers its browser-reachable callback URL and receives an opaque registration ID.
3. The worktree creates a provider authorization request using the fixed relay callback and a state value containing the registration ID and its own per-request value.
4. The provider redirects to the relay. The relay validates the registration lease and forwards the complete callback query to that worktree.
5. The worktree validates state and exchanges the code with the provider using the same fixed relay callback. The worktree keeps its tokens and refresh logic.

These steps describe the proposal. They are not commands or supported features in this initial repository.
