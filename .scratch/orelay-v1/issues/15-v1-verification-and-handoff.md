# 15: Verify the complete v1 workflow and prepare the release handoff

Status: resolved

Blocked by: [07: Recover predictably from Aspire and relay restarts](07-aspire-restart-recovery.md), [11: Diagnose problems and apply targeted CLI repairs](11-doctor-and-targeted-repairs.md), [12: Install and operate the Windows service from the same executable](12-windows-service.md), [13: Install and operate the Linux systemd service from the same executable](13-linux-systemd-service.md), [14: Produce and exercise native artifacts on Windows, macOS, and Linux](14-native-platform-artifacts.md).

Parent: [ORelay v1 specification](../spec.md#feedback-and-verification)

**What to build:** A contributor can follow the maintained verification skill and human documentation to prove the complete implemented v1 workflow, with a release-readiness record that distinguishes passed behavior from remaining gaps.

## Acceptance criteria

- [x] Audit the existing verification skill using `maintain-verification-skill`, including index hygiene, source coverage of every feature, and live execution of every mapped feature. Do not replace live coverage with build-only or source-only claims.
- [x] Exercise the real CLI and HTTP paths, multiple active worktrees with overlapping flows, raw callback query and state handling, lease/crash cleanup, the approved recovery behavior, config precedence and repairs, discovery, and the chosen shared topology.
- [x] Restore the packed hosting package into a consumer and verify its documented lifecycle through real AppHosts. The walkthrough includes worktree-owned state validation and direct synthetic code exchange with the fixed relay redirect URI.
- [x] Verify the implemented Windows and Linux service lifecycle and the native artifacts on every claimed supported platform. Record privilege, OS, network, or credential prerequisites for any unreachable path, along with the attempted operation.
- [x] Human documentation accurately covers installation, first use, command help, configuration, local/shared operation, package consumption, and troubleshooting. Agent workflow references and detailed verification procedures remain in their designated agent documents.
- [x] Capture sanitized commands, outputs, exit codes, resulting files/registrations, and artifact identities. Prove that cleanup removes only run-owned resources, includes failed attempts, and leaves evidence available afterward.
- [x] Record clean, changed, or blocked audit outcome with feature/platform coverage and outstanding defects or prerequisites. A product regression is reported as a product gap, not hidden by changing the documented expected behavior.
- [x] Prepare a release handoff identifying the built artifacts, verified package, supported platform matrix, known limitations, and remaining publication steps. Completion requires all required coverage or an explicitly accepted scope change; merely listing an unverified requirement is not a pass.

## Verification

Run the skill's documented launch, health check, driving, evidence, and cleanup procedure on the built version. Follow its required maintenance audit workflow, including bounded read-only feature reviews and coordinator-owned live driving. Re-run corrected verification recipes to prove them. Report focused prior evidence where still valid; repeat broader checks only for changed behavior or unresolved concerns.

## Scope

This is an integrated audit of behavior already tested in its implementation tickets. A maintenance-only pass edits the verification skill and reports product fixes separately. This ticket does not publish packages, merge or deploy code, create a public release, or start live provider grants.

## Delivery

Completed and reviewed on 2026-09-22. The verification maintenance audit outcome is changed. Independent read-only reviews covered all five mapped features; the orchestrator drove the live CLI, callbacks, config, Aspire, shared network, and platform CI paths. Verification-map drift was corrected and product failures were fixed in separate commits. All required native/service coverage passed. The release handoff is `docs/v1-handoff.md`; no external package or public release was published.

Local evidence paths above are relative to `.artifacts/verification/`. See [implementation evidence](../implementation-progress.md) and [native CI](https://github.com/demyte/ORelay/actions/runs/35713463965), verified at commit 5ffda0393eb41ab001ffffcbaacb9c56efdc26f9.
