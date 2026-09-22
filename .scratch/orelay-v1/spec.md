# ORelay v1 specification

Status: ready-for-agent

James approved the implementation plan and authorized parallel implementation, review, verification, and incremental commits to main on 2026-09-22. Package and public release publication remain separate actions.

## Confirmed implementation decisions

- Registration loss after relay restart or lease expiry requires an explicit restart of the affected application or AppHost to receive a new ID. Report degraded/restart-required state; do not automatically restart applications or add dynamic registration propagation. Temporary connectivity loss may retry while the registration remains valid.
- Shared-relay authentication is deferred. This version has no client credentials, API keys, or per-registration management secrets. Explicit shared binding permits remote clients to register, renew, and delete entries without authentication. Random routing IDs are identifiers, not an access-control claim.
- Keep loopback binding by default and validate exact HTTP/HTTPS destination URLs. Explicit shared operation permits non-loopback destinations. Do not require an allowlist, built-in certificate provisioning, or a particular protected transport to enable the user-requested shared mode. Operators can choose a network or TLS proxy externally.
- These decisions supersede the earlier access-control and recovery proposals below and in the planning breakdown. They are recorded as completed decisions in tickets 05 and 08.

See the [proposed ticket breakdown](breakdown.md) for delivery order and dependencies.

## Problem

OAuth providers require registered redirect URIs. Developers running several worktrees on different ports should not have to register every worktree's callback with each provider.

ORelay provides a fixed callback URL and routes each response to the worktree that began the authorization flow. It supports delegated API authorization, including integrations such as Xero. The worktree continues to own provider integration, state validation, code exchange, tokens, and refresh.

## Required outcome

One relay serves multiple active worktrees at once. A worktree registers its callback destination and receives an opaque registration ID. It includes that ID alongside its own per-request value in OAuth state. On callback, ORelay finds the live registration and redirects the browser to its destination with the complete provider query unchanged.

The relay can run on the developer's machine or at an explicitly configured shared address, including a Tailscale address. Aspire can own registration, lease renewal, and cleanup for a consuming AppHost. The same executable supplies the CLI, foreground server, and Windows or Linux service entry point.

## User stories

1. As a developer, I want one provider-registered callback URL so changing local ports does not require provider configuration changes.
2. As a developer, I want multiple active worktrees so I can run independent authorization flows concurrently.
3. As an integration author, I want the provider's full callback query preserved so my existing code can process success, denial, and provider-specific values.
4. As an integration author, I want to validate the full state in my worktree so routing does not replace my flow's correlation checks.
5. As a developer, I want stale registrations to expire so crashed worktrees do not accumulate forever.
6. As a developer, I want a stopped worktree to deregister promptly while other worktrees continue running.
7. As an Aspire user, I want the AppHost to register the allocated endpoint before my application starts.
8. As an Aspire user, I want renewal in the AppHost so pausing my API in a debugger does not by itself expire the registration.
9. As an Aspire user, I want an explicit recovery path after relay or resource restarts so I do not unknowingly use an obsolete registration ID.
10. As a shared-relay operator, I want explicit bind and advertised-address settings so agents on other machines can connect.
11. As a developer, I want callback discovery with explicit overrides so local and Tailscale configurations do not depend on guessed addresses.
12. As a developer, I want diagnostics to distinguish listener reachability from browser reachability so misleading probes do not hide a broken callback.
13. As an operator, I want all settings in a persistent configuration file that I can inspect and update through the CLI.
14. As an operator, I want a selected config path so container mounts and service accounts can use durable writable locations.
15. As an agent, I want comprehensive help, structured results, and stable exit behavior so I can operate ORelay unattended.
16. As a developer, I want first-run initialization and targeted doctor repairs so missing configuration is easy to fix.
17. As a Windows or Linux user, I want the CLI to install and control the service so I do not need a separate installer tool.
18. As a Windows, macOS, or Linux user, I want a self-contained Native AOT executable so I do not have to install .NET to run ORelay.
19. As a contributing agent, I want runnable formatting, lint, build, test, and AOT checks from the first working increment.
20. As a contributing agent, I want a maintained verification skill that drives actual user paths and preserves evidence after cleanup.

## Callback and registration behavior

- The callback registered with the provider is the relay's stable public callback URL. The worktree's callback destination can have a different port or host.
- Authorization and code exchange must use the same relay redirect URI where the provider requires it. The worktree must retain that URI when the browser arrives at its own destination. This follows the authorization-code redirect URI binding described in [OAuth 2.0 section 4.1.3](https://www.rfc-editor.org/rfc/rfc6749#section-4.1.3).
- Registration IDs identify relay entries and select the unauthenticated management target in this version. They are not OAuth state nonces or provider credentials.
- The proposed state envelope is `<registration-id>.<opaque-worktree-state>`. The implementation ticket must define the grammar, bounds, malformed-input behavior, and how an existing integration stores and validates the complete value. It must not depend on the opaque portion being JSON or having no delimiters.
- ORelay reads enough state to select a registration and forwards the original query. It must not replace state, decode and re-encode the callback query, drop unknown parameters, or reorder or merge repeated parameters.
- Missing, ambiguous, malformed, unknown, deregistered, or expired routing state fails explicitly without a fallback destination. Error responses and logs must not echo codes, full state, or other sensitive callback values.
- The registered destination is a validated exact HTTP or HTTPS URL with a path and no credentials, fragment, or existing query. Deliberate shared binding allows non-loopback destinations.
- Initial scope is browser redirects carrying a GET query. POST `form_post`, fragments, and other response modes require separate work.
- Route by live registration and never by a URL supplied directly in the callback. A registration cannot be retargeted while old flows still refer to it; a changed destination receives a new ID.
- Use an ordinary browser redirect. The relay does not fetch the destination or exchange the authorization code.
- The registry lives only in memory. Leases remove entries left by crashed worktrees. Graceful deregistration is idempotent and best effort; expiry remains the fallback.
- Recommended initial lease defaults are a five-minute lifetime and a one-minute renewal interval. Document allowed bounds, retry behavior, capacity limits, and the exact expiry boundary before shipping.
- A relay restart loses registrations. New sessions receive new IDs. Do not promise recovery of callbacks already in flight across a restart.
- Concurrent flows need distinct worktree state values even when they share one registration. Distinct relay registrations do not guarantee distinct grants or tenants at the provider.

Proposed management routes are `POST /registrations`, `PUT /registrations/{id}/lease`, and `DELETE /registrations/{id}`. The fixed callback path is proposed as `/callback`. These are design candidates, not existing endpoints. Each contract needs documented request and response fields, error codes, ownership, and expiry behavior.

## CLI and configuration

The executable is `orelay`, or `orelay.exe` on Windows. The agreed command families are:

| Command | Required behavior |
| --- | --- |
| `orelay server --port 12987 --bind 0.0.0.0` | Run in the foreground using explicit listener overrides. Loopback remains the default. |
| `orelay init` | Create missing configuration from defaults and supplied overrides; validate but preserve an existing file. |
| `orelay config get [key]` | Display effective configuration, including defaults. |
| `orelay config set <key> <value>` | Validate and persist a setting. |
| `orelay config clear <key>` | Remove the saved override so the built-in default applies. |
| `orelay doctor` | Diagnose without writing configuration or changing service state. |
| `orelay doctor --fix` | Apply documented repairs, including creation of missing configuration. |
| `orelay service install` | Install the same executable as the platform service with the selected config file. |
| `orelay service start/stop/restart/status/uninstall` | Control and inspect the owned Windows or Linux service. |

These commands are planned. They are not runnable yet.

- Store `orelay.json` beside the executable by default, independent of the working directory. Accept a global `--config-file <path>` on relevant commands, with documented relative-path resolution.
- Persist settings for the listener, advertised callback address, hostname or discovery selection, leases, and any later-approved management policy. Keep the configuration schema and defaults documented.
- Effective precedence is built-in defaults, then saved file values, then command-line overrides. Additional environment-variable overrides have not been agreed.
- Starting the server with a missing file writes defaults plus supplied setting overrides before serving requests. With an existing file, command-line overrides apply only to that invocation.
- `init`, `config set`, `config clear`, and documented `doctor --fix` repairs are explicit write operations. Proposed read-only commands, including `config get`, help, version, and doctor, do not create a file just by inspecting defaults.
- Preserve valid unrelated settings during changes. Invalid JSON, unknown keys, invalid values, and unsupported config versions produce actionable errors. Do not silently replace a malformed file.
- Write atomically and define how concurrent CLI writers avoid losing each other's changes. A permissions failure must name the selected path and leave existing data intact; do not fall back silently to another directory.
- The first version can apply settings at the next server start. Live reload is not required.
- Every command and option needs help, examples, defaults, and exit behavior. Support machine-readable JSON for commands returning data, keep diagnostics separate from structured stdout, and document the server's logging mode. Avoid interactive prompts in normal agent operation.
- Doctor reports what it actually checked and suggests exact next commands. Fix mode must distinguish successful repairs from remaining failures. Installing a service or changing network exposure remains an explicit command, not a surprise doctor repair.

## Networking and discovery

Binding and advertising are separate. A wildcard listener such as `0.0.0.0` is not a usable provider callback hostname. ORelay must know the advertised relay callback URL, while each worktree registration must contain the destination the browser can reach.

- Default to a loopback listener and local callbacks. Permit explicitly configured non-loopback listeners and destinations for shared development environments under the agreed management policy.
- An explicit advertised URL or callback URL wins over discovery. Do not infer a trustworthy external URL from an arbitrary Host header or registration source address.
- In local mode, derive the worktree destination from the Aspire endpoint and callback path where that endpoint is suitable for the browser.
- In Tailscale mode, discovery runs where the destination application lives. It can combine a discovered local Tailscale hostname or IP with the application's actual listening port and callback path. The CLI provides machine-readable [Tailscale status output](https://tailscale.com/docs/reference/tailscale-cli#status) for discovery; do not require Tailscale for ordinary loopback use.
- Selecting a hostname does not change a listener binding. Report a destination that advertises a reachable host but leaves the application bound only to loopback.
- Ambiguous or unavailable discovery reports candidates or the missing prerequisite and accepts an explicit override. Do not guess which interface the browser can reach.
- Provider-to-relay navigation and relay-to-worktree navigation happen in the user's browser. A successful probe from the relay proves only relay-side connectivity, not browser-side reachability.
- Configuration must distinguish relay-server settings from per-worktree inputs. A central server's config must not pretend to describe every client's local Tailscale or Aspire endpoint.

## Aspire package and recovery

`ORelay.Aspire.Hosting` is a separate NuGet package referenced by a consuming AppHost. ORelay's executable remains independently runnable and can serve multiple AppHosts. Shutting down a consuming AppHost must not stop the shared relay.

The integration resolves a resource's allocated endpoint, registers it before the application starts, provides the registration ID and fixed relay callback URI to the application, renews from the AppHost, and deregisters when the resource or AppHost stops. Use the pinned Aspire version's supported lifecycle events and prove the ordering with a real consuming AppHost. [Aspire's eventing documentation](https://aspire.dev/app-host/eventing/) describes endpoint-allocation, pre-start, and resource-stop hooks.

The application still needs code that constructs and validates its OAuth state. AppHost configuration alone cannot transparently change every application's existing OAuth implementation.

Recovery uses the selected explicit restart-required contract. Startup environment variables do not change inside an already-running application. Unknown or expired registration produces degraded state and requires a manual resource or AppHost restart; the next process receives a new registration for its allocated endpoint. Short disconnections retry only while the known lease remains valid. Pending old flows fail, and stopping a resource cancels renewal and recovery.

Do not add an application runtime NuGet package, stable persistent registrations, or automatic application restarts without agreeing that behavior.

## Build, layout, and distribution

Use .NET 10, ASP.NET Core Minimal API, and Kestrel for the executable. Keep the CLI and foreground/service host in the same executable. CLI library selection must be proven compatible with Native AOT rather than assumed.

The user-required layout and repository files are:

```text
global.json
Directory.Build.props
Directory.Build.targets
Directory.Packages.props
LICENSE                         MIT
src/ORelay/
src/ORelay.Aspire.Hosting/
packaging/windows/
packaging/linux/
```

Add test and sample projects only where needed to demonstrate the contracts. The shared build files need purposeful settings, central dependency versions, a pinned SDK, and scoped targets that do not impose executable-only publish behavior on the AppHost library or tests.

Native AOT, self-contained, single-file execution is a requirement from the first executable increment on Windows, macOS, and Linux. Each OS and architecture has its own binary. The writable configuration file is separate application data; it does not turn distribution into a multi-file .NET runtime deployment. Debug symbols and documentation can be separate optional release assets. Native AOT includes the .NET runtime but still has platform prerequisites, so document the supported OS baseline. See [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

Build native artifacts on matching OS runners. Do not assume Windows can produce tested native binaries for Linux and macOS. [Native AOT cross-compilation guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) distinguishes cross-architecture support from cross-OS builds.

The proposed artifact matrix is x64 and ARM64 for each required OS. Confirm the release matrix and available verification runners before claiming each combination is supported. At minimum, all three required operating systems must have an exercised native executable.

Windows service and Linux systemd lifecycle support are in scope. macOS direct execution is required; launchd service installation has not been requested. Service installation records absolute executable and configuration paths and handles service-account permissions explicitly. Uninstall removes only owned service integration and preserves saved configuration unless deletion is separately requested.

NuGet packing and consumption from a local package feed are in scope. Publishing packages or creating a GitHub release remains a separate action.

## Feedback and verification

Feedback is part of each implementation increment, not a last-ticket addition. An agent must have documented formatting or lint checks, build, focused tests, and a host-appropriate Native AOT publish command. Check actionable failures and meaningful exit codes. Broad repeat testing is unnecessary once relevant checks pass without new concerns.

Use the public CLI and HTTP behavior as the main verification points. Add a real consuming AppHost for lifecycle behavior, real service managers for service behavior, and published executables for native packaging. There is no existing test infrastructure to copy yet.

Create `.agents/skills/verify-orelay/SKILL.md` from actual working commands using the requested `create-verification-skill` workflow as soon as the first CLI works. Include Launch, read-only Doctor, Drive, Evidence, Cleanup, and executable Helpers where needed. Seed the feature map with actual implemented behavior; add each later user-facing feature in the same change that implements it.

Prove the generated skill by following its instructions, capturing an action and its observable result, cleaning up, and confirming evidence remains. Follow `maintain-verification-skill` for later full audits. A source-only or build-only pass must not claim live verification.

Required scenarios, added as their features arrive:

- Several simultaneous worktrees and overlapping flows with no cross-routing. Preserve encoded values, repeated provider parameters, empty values, and error callbacks. Reject ambiguous routing state.
- A complete synthetic delegated authorization flow, including worktree state validation and a direct worktree code exchange using the relay redirect URI. No routine verification needs live Xero grants.
- Expiry, renewal, graceful deregistration, process crashes, relay restart, and destination port reuse without delivery to a new unrelated session.
- Missing and malformed config; explicit config paths; persistence versus transient overrides; clear-to-default; read-only commands; concurrent writes; write failures.
- Local and shared-relay concurrent registrations, malformed destinations, invalid operations, explicit binding, discovery success and failure, and honest reachability diagnostics. Management authentication is intentionally absent in this version.
- Aspire startup ordering, multiple AppHosts, stopped and restarted resources, and the approved recovery behavior.
- Service install, start, stop, restart, status, and uninstall on actual Windows and Linux environments, plus native executable operation on each claimed platform.

Use run-owned configuration, ports, processes, and registrations. Record sanitized commands, stdout/stderr, exit codes, redirects, and file or registry results. Keep evidence in `.artifacts/verification/<run-id>/` and temporary state in `work/verification/<run-id>/`. Cleanup must retain evidence, include failed attempts, and never stop a user's unrelated service. Report missing platform, privilege, network, or credential prerequisites as unverified coverage.

Keep README content for humans installing, using, and contributing. Keep agent constraints and workflow references in AGENTS. Detailed live verification recipes belong in the verification skill.

## Decisions still needed

| Decision | Why it matters | Recommendation to evaluate |
| --- | --- | --- |
| Release architectures and minimum OS versions | Native build success alone does not establish runnable support. | Evaluate x64 and ARM64 on each OS and publish only combinations with native execution evidence. |

These decisions gate only affected work. They do not block the first CLI, config, or loopback callback increments. Exact parser libraries, test framework, error-code numbers, and private types are implementation choices, provided the public behavior above remains intact.

## Out of scope

- Application sign-in, an identity provider, an OAuth grant broker, token exchange through ORelay, or token storage.
- Guaranteed independent provider grants for separate worktrees.
- Durable registrations, a database, a management web UI, a hosted SaaS offering, or transparent migration of flows across a relay restart.
- Arbitrary OAuth response modes beyond the initial GET-query callback.
- Automatic network changes, Tailscale installation, certificate provisioning, Docker deployment, or changes to a consuming production integration.
- macOS launchd integration, package-store submission, public release publication, or running implementation tickets during this planning task.
