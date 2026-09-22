# ORelay v1 handoff

The v1 CLI, relay, configuration, discovery, doctor, Aspire hosting package, and Windows/systemd service implementations are complete in `main`. All six native platform jobs and the Windows/Linux service checks passed at source commit `5ffda0393eb41ab001ffffcbaacb9c56efdc26f9` on 2026-09-22.

## Artifacts

The [successful native workflow run](https://github.com/demyte/ORelay/actions/runs/35713463965) supplies `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each job supplies a RID-named archive, SHA-256 checksum, MIT license, and separate verification evidence. The [platform guide](native-platforms.md) records the build and execution baselines. Artifacts are subject to the repository's CI retention period; save the selected archive and checksum together.

Download the archive matching the host's OS and architecture. Extract it, check its SHA-256 against the companion checksum, and run `orelay --version` or `orelay.exe --version`. Unix archives preserve the executable bit. Keep the executable in a stable location when installing a service. Use `--config-file` for a writable configuration location.

`ORelay.Aspire.Hosting` packs independently. The verified local package is `0.1.0-audit.20260922191012`, consumed by the sample AppHost and its integration tests from the local feed. The consumer uses this repository's build configuration. No NuGet package has been published. [Package usage](../src/ORelay.Aspire.Hosting/PACKAGE.md) describes the hosting API and required application changes.

## Verified application behavior

- The published CLI initializes, reads, updates, and clears config, preserves invocation precedence, and leaves existing data intact when replacement is blocked. Two separate CLI processes preserve concurrent updates to different settings.
- Three simultaneous callback listeners receive their own exact raw queries. Renewal, expiry, deletion, malformed state, and duplicate state have distinct outcomes without fallback routing.
- Real AppHosts consume the packed library, complete overlapping synthetic delegated flows, validate full state, and exchange codes directly with the provider. Tampered state is rejected and the original browser-correlated flow still completes.
- AppHost renewal continues while an API is suspended. Killing an AppHost lets its orphaned registration expire; restarting on reused or changed ports creates fresh IDs. Relay restart invalidates pending flows until the resource is explicitly restarted.
- Doctor diagnoses missing and malformed config, a directory used as a file, and a foreign listener on the requested port. Read-only runs preserve config and listeners. Missing-file repair is repeatable and reports any remaining health failure.
- Tailscale discovery used the installed daemon. Two concurrent requests from a separate Tailscale host reached the Windows relay and registered destinations. That host could not resolve the discovered MagicDNS hostname; an explicit Tailscale IP hostname override completed the flows without network-setting changes.
- Each native executable ran alone while the .NET shared runtime was temporarily unavailable. The managed control failed as expected, and the native CLI completed configuration, startup, registration, and an exact callback. The runtime directory was restored afterward.
- Windows SCM and Linux systemd passed the full service lifecycle, including paths with spaces and dollar signs, conflicting definitions, repeated operations, callback delivery, registration loss on restart, config preservation, and cleanup. Unprivileged installation failed without creating a service.

The [standard CI run](https://github.com/demyte/ORelay/actions/runs/35713464067) built without warnings and passed 93 core tests and 12 hosting tests. Its two integration tests are skipped unless their owned-relay prerequisites are supplied. A separate packed run executed all 14 hosting tests with no skips, followed by the added live state-mismatch assertion. Synthetic flows establish the relay/package behavior; a live Xero grant was not used.

## Operational limits

Management authentication is deferred, including in shared mode. Registrations remain in memory and can outlive a crashed worktree until their leases expire. Applications need an explicit restart after registration loss. Only GET callbacks with query parameters are supported.

The native OS claims apply to the tested runner baselines. Linux artifacts use glibc; musl and older distribution baselines have not been established. macOS supports direct execution, with no launchd installer. HTTPS termination, firewall changes, Tailscale installation, provider registration, signing, and notarization remain external operations.

## Publication steps

Choose release and package versions, rebuild the artifacts from the chosen tag, and retain checksums and verification evidence. Publish the NuGet package and release archives only when requested. Signing, notarization, and package-manager distribution can be added separately. Nothing in this delivery publishes a public release or grants provider access.
