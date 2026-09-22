# ORelay agent instructions

ORelay is a local-development OAuth 2 authorization callback relay for delegated API access. Keep the project small and provider-agnostic. Its job is to route callbacks to registered local worktrees. Provider grant and consent behavior, code exchange, token storage, and token refresh belong to the worktree or provider integration, not to the relay.

Treat the relay core, Aspire hosting integration, and operating-system packaging as separate concerns. Do not add a database or token store for the in-memory registration design. Default to a local-only listener and loopback callback destinations. Reject expired or unknown registration IDs without choosing a fallback destination.

The README is a design proposal until code and tests implement a behavior. Keep proposed names, endpoints, package layouts, and commands marked as planned. Do not describe restart reconnection or registration-ID propagation as solved until the mechanism is implemented and tested.

When implementation begins:

- Keep callback routing opaque. Preserve the complete callback query for the registered worktree, including state, code, error fields, and other provider values.
- Protect callback query strings, authorization codes, access tokens, refresh tokens, and secrets from logs, diagnostics, exceptions, and test output.
- Add meaningful focused tests for routing, concurrent registrations and callbacks, lease renewal and expiry, unknown registrations, and failure paths. Avoid tests that only mirror private implementation details.
- Make build, run, and test instructions match the actual repository. Do not document commands that have not been implemented.
- Keep changes within the relay scope unless James explicitly authorizes a broader feature or service installation.

Do not select a license, publish packages, or create external service configuration without an explicit request.
