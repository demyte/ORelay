# Callback relay and leases

## Sub-features

- Register independent loopback callback destinations.
- Redirect a callback without changing its raw query.
- Renew one lease while another expires.
- Delete a registration and reject later callbacks.
- Reject malformed, unknown, and expired routing state without a fallback.
- Show timestamped server activity without logging callback values or destination paths.

## How to get to it (user POV)

Start the foreground server with a run-owned config and port. A worktree posts its callback URL to `/registrations`, stores the returned opaque ID, and prefixes its opaque state with `<id>.`. The provider callback goes to `/callback`; the worktree receives the redirect and owns state validation and code exchange.

`/callback` is the default. A `publicUrl` path prefix changes the served callback path. Use the returned `relayCallbackUrl` when driving a configured deployment; do not assume the default path.

```powershell
orelay --config-file .run\orelay.json server --port 13871
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:13871/registrations -ContentType application/json -Body '{"callbackUrl":"http://127.0.0.1:13872/oauth/callback"}'
Invoke-WebRequest -MaximumRedirection 0 'http://127.0.0.1:13871/callback?state=<id>.opaque&code=synthetic'
Invoke-WebRequest -Method Put 'http://127.0.0.1:13871/registrations/<id>/lease'
Invoke-WebRequest -Method Delete 'http://127.0.0.1:13871/registrations/<id>'
```

## Driving it with PowerShell

The helper starts a real Native AOT process and two run-owned `TcpListener` callback destinations. It posts both registration requests concurrently, follows both redirects, and compares each destination request target with the original raw query. It then renews only the first registration, waits through the second lease, proves the second callback returns `404`, and proves the renewed callback still redirects. Finally it deletes the live registration twice and checks the next callback returns `404`.

For full lease acceptance, use at least three independent destination listeners with overlapping callbacks. Renew one, let a second expire, and delete the third, then prove the outcomes are 302, 404, and 404 respectively. Also send duplicate `state` parameters for a live ID and expect 400 `invalid_routing_state`, no `Location`, and no callback values in the response. The existing public-HTTP test includes the duplicate-state assertion; the routine helper's two-listener run is a smaller recurring check.

## Console logging

Run `.github/workflows/logging-smoke.ps1 -ExecutablePath <published-executable> -RunRoot <run-owned-directory>` after publishing. Use a fresh directory under `.artifacts/verification/` for local evidence. It starts separate plain-text and JSON server processes, drives registration and callback requests with synthetic secret sentinels, and checks captured logs for useful events, no secret values, and no ANSI escapes when redirected. JSON mode must keep the readiness object on stdout and parseable log objects on stderr. The native platform workflow runs this check for every RID.

For terminal colour, run the published `server` with a disposable config and port in a real terminal. Confirm coloured levels, then stop it with Ctrl+C and confirm the shutdown message. Repeat with `NO_COLOR=1` and check that no colour escapes are emitted. This interactive check does not replace the captured-output and redaction checks.

Sources: `src/ORelay/Server/RelayServerHost.cs`, `RelayServerLog.cs`, and `RelayServerEndpoints.cs`.

## Gotchas

- Loopback server mode allows loopback callback destinations. Non-loopback destinations require an effective non-loopback bind supplied in saved configuration or invocation flags.
- Persistent registration tests cover conflicting store and per-call destination policies. A call cannot widen the store's policy, and an accepted remote registration must remain readable and renewable after reopening with the same policy.
- The registration ID is a routing identifier, not a credential. Management access is open in this version.
- The selected config path determines the persistent SQLite file. A relay restart preserves unexpired registrations; expiry during downtime still prevents routing and renewal. The callback query is never exchanged for a token by ORelay.
- `QueryString.Value` is the raw input used for the redirect. Do not replace this proof with a parsed dictionary comparison.
