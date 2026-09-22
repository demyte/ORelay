# 10: Discover worktree callback addresses with explicit overrides

Status: ready-for-agent

Blocked by: [06: Register a real Aspire application using the hosting NuGet package](06-aspire-hosting-package.md), [09: Run a shared relay with explicit network configuration](09-shared-relay-networking.md).

Parent: [ORelay v1 specification](../spec.md#networking-and-discovery)

**What to build:** A worktree selects the exact browser-reachable callback URL using its Aspire endpoint, optional local Tailscale discovery, or explicit overrides, and registers that resolved destination with the relay.

## Acceptance criteria

- [ ] Define the supported discovery modes and explicit URL/hostname overrides in the CLI and hosting package. Full explicit URLs take precedence; document how hostname overrides combine with an actual endpoint's scheme, port, and path.
- [ ] Resolve local callbacks from the resource's allocated endpoint and configured path. Preserve correct URI construction for hostnames, IPv4, and IPv6 rather than concatenating ambiguous address strings.
- [ ] Optional Tailscale discovery runs on the destination application's host and uses its actual endpoint details. A central relay does not substitute its own interface information for a remote worktree's address.
- [ ] Keep relay-server advertised settings separate from per-worktree discovery inputs. Apply persistence and invocation precedence consistently for settings owned by the CLI.
- [ ] Ordinary local mode works without Tailscale. Missing tools, unavailable daemon state, unsupported status data, and ambiguous candidates produce actionable failures or candidates with an explicit override path; they do not select a guessed destination silently.
- [ ] Detect or explain incompatible application bindings, including a remotely advertised hostname for a loopback-only listener. Selecting a host must not implicitly change the application's network exposure.
- [ ] The exact resolved destination is subject to the relay's approved policy before registration. Diagnostics distinguish local discovery, any network probes, and actual browser reachability.
- [ ] Document examples and JSON result/error behavior. Extend focused discovery tests and the live verification map, preserving the callback query and worktree state contract.

## Verification

Exercise a local Aspire endpoint, an explicit full URL, a hostname override, and a real Tailscale destination in an owned environment. Separately prove missing-tool, unavailable/ambiguous discovery, and incompatible-listener behavior. Follow a browser redirect to a remote worktree from the declared browser network position. Stub external discovery output only at the process boundary for focused edge-case tests; record unavailable live prerequisites as unverified coverage.

## Scope

Discovery selects and explains addresses. It does not install Tailscale, configure network adapters or firewalls, register URLs with providers, or guarantee reachability from an untested browser.
