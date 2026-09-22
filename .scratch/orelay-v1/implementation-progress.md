# Implementation and verification record

All 15 v1 tickets are delivered, reviewed, and verified on 2026-09-22. Source commit `5ffda0393eb41ab001ffffcbaacb9c56efdc26f9` passed [standard CI](https://github.com/demyte/ORelay/actions/runs/35713464067) and [all six native platform jobs](https://github.com/demyte/ORelay/actions/runs/35713463965). The later ticket and documentation commits record this result without changing the executable.

The orchestrator reviewed and integrated bounded subagent work, drove the final verification, and committed each reviewed increment to `main`. James's decisions are implemented: registration loss requires an explicit resource or AppHost restart, and shared management authentication is deferred.

## Review and feedback

The solution builds with analyzers enabled and no warnings. Standard CI passed 93 core tests and 12 hosting tests, with two integration tests explicitly skipped because that job does not supply their live-relay prerequisites. A dedicated packed-library run passed all 14 hosting tests without skips. Tests check public behavior and failure paths, including real HTTP callbacks, lifecycle loss, and concurrent updates.

Reviews corrected URL and path validation, IPv6 advertisement, unreadable config handling, conservative renewal deadlines, and service identity and ownership/error handling. Actual CI found Windows service configuration encoding and never-started status errors, Linux unit path handling, and a startup hang when systemd supplied `/` as the working directory. Separate commits fixed those defects. The final native workflow proves the corrected behavior on the real managers.

## Verification maintenance audit

Outcome: **changed**.

The `maintain-verification-skill` pass reviewed every source-backed feature map. Separate read-only reviews covered CLI/configuration, relay/leases, Aspire, discovery/doctor, and native artifacts/services. The orchestrator exercised every feature live, using local processes, actual AppHosts, a separate Tailscale host, and disposable native CI runners.

Map corrections describe AppHost-owned renewal, callback discovery, full-state rejection, crash/port-reuse checks, custom callback paths, three-listener lease isolation, occupied-port diagnosis, mandatory service proof, permission failures, and runtime-absence controls. Product regressions were fixed in source rather than changing expected outcomes to match failures.

## Retained evidence

The paths below are relative to `.artifacts/verification/`, which is intentionally ignored by Git. Scripts, commands, synthetic HTTP results, process identities, hashes, and cleanup records remain available locally. The final CI artifacts also retain platform evidence in GitHub.

| Evidence directory | What passed |
| --- | --- |
| `root-final-02` | Final Windows Native AOT executable, help/version/errors, config init/get/set/clear and precedence, two exact callbacks, leases, deregistration, read-only doctor, and cleanup. Executable SHA-256 `196A1753FACFFE1517AF4B864FD69EF77C255377AF27D1F4AF7C688127B71C07`. |
| `root-config-dda3f6ac` | Two separate native CLI writers preserve both updates; repeated init preserves existing config; denied replacement leaves the old hash intact. |
| `root-doctor-ca3af87c` | Missing/malformed config, a directory selected as a file, read-only behavior, missing-file repair, and repeat repair. |
| `root-edges-a018da3b` | Foreign occupied port diagnosed without changing config or listener; duplicate state rejected without redirect or sensitive echo; valid callback still succeeds. |
| `root-three-abe2e87c` | Three independent listeners receive overlapping exact queries. Renewing one, expiring another, and deleting the third gives 302, 404, and 404. |
| `root-aspire-fe7a88d7` | Packed `ORelay.Aspire.Hosting` version `0.1.0-audit.20260922191012`, consumed from the local feed by real AppHosts; all 14 tests pass. Covers overlapping synthetic OAuth/code exchange, paused API renewal, relay restart, explicit recovery, and shutdown isolation. |
| `aspire-crash` | External AppHost process-tree termination allows lease expiry while relay stays alive; reused/changed API ports receive fresh IDs. Live tampered full state returns `invalid_state`, then the original browser-correlated flow succeeds. |
| `root-remote-042b7b4f` | Two concurrent synthetic browser clients on `jimbob-pi` reach the Windows relay and callbacks over Tailscale using an explicit reachable IP hostname override. |
| `ci-5ffda03/artifacts` | Six matching-host Native AOT executions with unavailable shared .NET runtime and a failing managed control, exact callbacks, runtime restoration, RID archives, checksums, and MIT license. Windows/Linux x64 also pass real service lifecycle, insufficient privilege, config preservation, and owned-service cleanup. |
| `streen-contentroot` | Linux startup reproduction before and after explicitly setting the host content directory. Managed build from `/` hung before the fix and started after it; final native systemd CI supplies the platform proof. |

Package consumption uses this repository's build configuration. It does not claim an unrelated external consumer build. Synthetic provider flows establish application-owned state validation and direct code exchange; no live Xero grant was used.

## Failed attempts and cleanup

Earlier failed CI runs remain available with diagnostics. Their failures were reproduced or traced, fixed, and superseded by the successful final run. `root-remote-ce099b06` retains the remote MagicDNS-resolution failure. The IP override passed without DNS or firewall changes. `root-config-f3b4a77f` records an ACL-based attempt that could not be applied; it does not count as permission coverage. The later file-replacement failure and CI service-permission checks supply the relevant proof.

Verification stopped its owned relay/AppHost/API processes and callback listeners. CI restored the .NET runtime directories, removed its service definitions and temporary Windows account, and retained config/evidence as intended. No developer-installed service, saved user config, or provider grant was changed. Completed scratch directories were removed after small evidence files were retained in `scratch-retained`; its `cleanup.json` lists the exact paths. The inactive `work/verification/streen-contentroot` reproduction directory remains because the execution guard rejected its deletion. Its scripts are also retained beside the evidence, and no owned process remains.

All required feature and platform coverage passed. Remaining operational prerequisites and publication steps are documented in the [v1 handoff](../../docs/v1-handoff.md). No public release or NuGet package has been published.
