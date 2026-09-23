# HTTP and state contract

All management routes are at the relay's root URL. They have no authentication in v1. A client that can reach them can create, renew, or delete registrations. The browser must be able to reach both the advertised relay callback and the registered destination.

## Registrations

Send `POST /registrations` with `Content-Type: application/json`:

```json
{"callbackUrl":"http://127.0.0.1:5017/oauth/callback"}
```

A successful response has HTTP status `201` and these JSON fields:

| Field | Meaning |
| --- | --- |
| `id` | Opaque routing ID, currently 43 base64url characters from 32 random bytes. |
| `callbackUrl` | Exact destination supplied in the request. |
| `expiresAt` | UTC lease expiry in ISO 8601 form. |
| `leaseSeconds` | Lease duration in seconds. |
| `relayCallbackUrl` | Fixed provider redirect URI to use for authorization and code exchange. |

The destination must be an absolute HTTP or HTTPS URL of at most 2048 characters. Credentials, query strings, fragments, whitespace, control characters, wildcard hosts, and port zero are rejected. Loopback mode accepts `localhost` and loopback IP addresses. An explicit non-loopback bind permits remote destinations. ORelay preserves the supplied destination and never retargets an existing registration.

Send `PUT /registrations/{id}/lease` without a body to renew a live registration. HTTP `200` returns the same fields with a new expiry. Renew before expiry, allowing for network latency. Renewal cannot restore an expired registration. The default lease lasts 300 seconds and the default capacity is 1000 registrations; both are configurable.

An entry is expired when the relay's current time is equal to or later than `expiresAt`. Registry operations are atomic; a callback already accepted for routing can finish its redirect while another operation deletes the entry. A crashed worktree's lease can remain live until expiry. The Aspire package renews every third of a lease, capped at 60 seconds, and bounds retries by the remaining lease.

Send `DELETE /registrations/{id}` to deregister. HTTP `204` is returned even when the ID is already absent. The relay commits registrations, renewals, and deletions to its SQLite database before acknowledging them. Unexpired registrations survive a process restart with the same IDs and deadlines. Startup and periodic cleanup remove expired rows, and each request checks expiry independently of cleanup. Restarting the relay never extends a lease. Applications whose registration expires or disappears obtain a new registration after an explicit restart and begin pending authorization flows again.

## Callback state

Use this decoded OAuth state value:

```text
<registration-id>.<opaque-worktree-state>
```

The first dot separates the routing ID. Accepted IDs contain 32 to 128 ASCII letters, digits, hyphens, or underscores. The suffix must be nonempty and may contain more dots. The complete decoded state is limited to 4096 UTF-16 code units. Provider or HTTP request limits can be lower.

The worktree generates unpredictable state, binds it to the initiating browser, stores and validates the complete value, and consumes the completed flow once. ORelay parses only the prefix and does not provide OAuth state validation. The sample demonstrates browser correlation, mismatch rejection, and direct code exchange with a synthetic provider.

The provider sends `GET /callback` with exactly one nonempty `state` query parameter. A configured public URL with a path prefix changes this callback route accordingly. For a live registration, ORelay responds `302` with `Location` set to the exact destination plus the original raw query. It preserves encoding, ordering, empty values, unknown fields, repeated non-state parameters, and provider errors. It does not fetch the destination or exchange the code. POST-based response modes are not supported.

## Results and errors

| Operation or condition | HTTP result |
| --- | --- |
| `GET /health` | `200`, `{"identity":"orelay","status":"ok"}`. |
| Missing callback destination | `400`, `invalid_request`. |
| Invalid destination | `400`, `invalid_callback_url` or `callback_must_be_loopback`. |
| Registration capacity reached | `503`, `capacity_exceeded`. |
| Missing, duplicate, malformed, or oversized state | `400`, `invalid_routing_state`. |
| Unknown, deleted, or expired ID during callback or renewal | `404`, `registration_not_found`. |

Application errors use `{"code":"...","message":"..."}` and do not echo callback values. Malformed JSON, unsupported content types, invalid HTTP requests, and unsupported methods may be rejected by the HTTP host before this application error contract applies. Errors never redirect to a fallback destination. Responses disable caching, and normal server logging excludes callback query values.
