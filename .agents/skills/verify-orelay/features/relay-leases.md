# Callback relay and leases

## Sub-features

- Register independent loopback callback destinations.
- Redirect a callback without changing its raw query.
- Renew one lease while another expires.
- Delete a registration and reject later callbacks.
- Reject malformed, unknown, and expired routing state without a fallback.

## How to get to it (user POV)

Start the foreground server with a run-owned config and port. A worktree posts its callback URL to `/registrations`, stores the returned opaque ID, and prefixes its opaque state with `<id>.`. The provider callback goes to `/callback`; the worktree receives the redirect and owns state validation and code exchange.

```powershell
orelay --config-file .run\orelay.json server
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:13871/registrations -ContentType application/json -Body '{"callbackUrl":"http://127.0.0.1:13872/oauth/callback"}'
Invoke-WebRequest -MaximumRedirection 0 'http://127.0.0.1:13871/callback?state=<id>.opaque&code=synthetic'
Invoke-WebRequest -Method Put 'http://127.0.0.1:13871/registrations/<id>/lease'
Invoke-WebRequest -Method Delete 'http://127.0.0.1:13871/registrations/<id>'
```

## Driving it with PowerShell

The helper starts a real Native AOT process and two run-owned `TcpListener` callback destinations. It posts both registration requests concurrently, follows both redirects, and compares each destination request target with the original raw query. It then renews only the first registration, waits through the second lease, proves the second callback returns `404`, and proves the renewed callback still redirects. Finally it deletes the live registration twice and checks the next callback returns `404`.

## Gotchas

- Loopback server mode allows loopback callback destinations. Non-loopback destinations require explicit shared binding in the configuration.
- The registration ID is a routing identifier, not a credential. Management access is open in this version.
- A relay restart loses registrations. The callback query is never exchanged for a token by ORelay.
- `QueryString.Value` is the raw input used for the redirect. Do not replace this proof with a parsed dictionary comparison.
