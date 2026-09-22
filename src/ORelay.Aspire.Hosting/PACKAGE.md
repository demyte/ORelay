# ORelay Aspire hosting

Reference this package from the AppHost only. Start the shared ORelay server separately.

```csharp
using ORelay.Aspire.Hosting;

var relay = builder.AddORelay("relay", new Uri("http://localhost:12987"));
builder.AddProject<Projects.Api>("api")
    .WithORelay(relay, "/oauth/callback", endpointName: "http");
```

The callback uses the selected Aspire endpoint's allocated URL. Supply `callbackUrl` to override the complete browser-reachable destination. Each registered resource must have one instance. Give separate worktrees separate resources, rather than replicas. The package registers before startup, injects `ORelay__RegistrationId` and `ORelay__RedirectUri`, renews from the AppHost, and deletes the registration when the resource or AppHost stops. It never stops the shared relay. The management server URL must use the root path.

For worktree hostname selection, pass `callbackOptions`:

```csharp
using ORelay.Discovery;

api.WithORelay(relay, "/oauth/callback", callbackOptions: new()
{
    AutoDiscovery = CallbackDiscoveryMode.Tailscale,
});
```

Selection order is the complete `callbackUrl`, then `callbackOptions.Hostname`, then Tailscale discovery when requested, then the allocated endpoint for local or no-discovery mode. Hostname selection keeps the allocated port and supplied callback path. Tailscale runs on the AppHost machine once for each registration session, with a ten-second timeout. An unavailable or ambiguous result blocks registration and reports the next step and candidates. An optional `TailscaleProvider` can supply the status process boundary.

Selecting a host does not change a listener. When Aspire's allocated binding is known to accept only loopback connections, a discovered or overridden remote hostname fails with an actionable binding error. Configure a reachable listener or supply a complete URL for an independently configured forwarding endpoint. The complete URL is an explicit override, not a reachability guarantee. Browser reachability still needs verification.

The application constructs state as `<registration-id>.<random-worktree-state>`, stores and validates the complete value, and exchanges the code directly with its provider. Use the injected fixed relay redirect URI for both authorization and code exchange. This package cannot update an arbitrary application's OAuth implementation.

Renewal runs every one third of the lease, capped at 60 seconds. Connection failures retry the existing ID every five seconds, bounded by the last acknowledged lease. Unknown registration or lease expiry sets the resource health to degraded with a restart instruction and stops retries. The running application still has its original environment. Do not begin another flow until you explicitly restart that resource or the AppHost. There is no automatic re-registration or application restart. A restarted resource gets a new ID. Pending flows using a lost ID fail.

The integration uses Aspire 13.5.2 public endpoint-allocation, pre-start, environment and resource-stop callbacks. Initial failure blocks startup. Graceful cleanup is best effort, with lease expiry covering crashed AppHosts.
