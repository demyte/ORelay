# 14: Produce and exercise native artifacts on Windows, macOS, and Linux

Status: ready-for-agent

Blocked by: [03: Route a delegated callback through a local relay](03-local-callback-relay.md).

Parent: [ORelay v1 specification](../spec.md#build-layout-and-distribution)

**What to build:** A user receives a native executable for the supported OS and architecture, with evidence it runs without an installed .NET runtime and with clear platform prerequisites.

## Acceptance criteria

- [ ] Build self-contained, single-file Native AOT artifacts on matching Windows, macOS, and Linux environments. Keep these required OS targets in the matrix; one host's build is not proof for all three.
- [ ] Evaluate the proposed x64 and ARM64 targets and record the exact release architecture matrix and minimum OS baselines. If required target coverage is unavailable, report that blocker rather than silently reducing the required OS scope.
- [ ] For each claimed supported combination, execute the published artifact in a suitable environment without an installed .NET runtime. Confirm it does not require adjacent managed runtime files or runtime extraction to operate.
- [ ] Verify help/version, isolated configuration initialization, foreground startup, registration, and an actual synthetic callback through each claimed target's native executable.
- [ ] Produce reviewable archives with unambiguous OS/architecture names, checksums, license, and documented prerequisites. Treat debug symbols as optional separate assets and config as writable application data.
- [ ] Extend CI with native build and execution jobs and retain relevant evidence. Keep these jobs exercising later feature changes as they land. Do not turn off AOT/trimming warnings globally to achieve green builds.
- [ ] Document how to select and run the appropriate artifact. Distinguish compile-only, native-executed, and unavailable targets. Do not claim macOS service installation as part of macOS direct-execution support.
- [ ] Add the native artifact paths to the verification map using actual build outputs and commands established by implementation.

## Verification

Collect build and execution evidence for every claimed OS/architecture combination, including artifact identity, environment baseline, file inventory, CLI output, readiness, callback result, and cleanup. Keep supported-host library prerequisites explicit. Use disposable environments instead of removing runtimes from a developer's workstation. A successful cross-compile without target execution remains incomplete support evidence.

## Scope

Produce artifacts and release instructions. External release uploads, NuGet publication, code signing, notarization, package-store submission, and macOS service installation are separate actions. The exact architecture support record is completed here, not assumed from the proposed matrix.
