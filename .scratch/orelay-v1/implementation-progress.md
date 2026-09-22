# Implementation progress

## Reviewed local increment, 2026-09-22

Implemented the CLI/configuration store, in-memory callback relay and leases, shared binding, discovery/doctor, Aspire package and sample, and Windows/systemd service adapters. The first implementation commit has local Windows native proof; platform and service-manager verification remain open until the native CI jobs complete.

Review corrections include callback URL/path validation, IPv6 advertisement, unreadable config handling, conservative lease deadlines, custom service identity propagation, systemd path escaping and unit naming, and bounded service-manager processes. Independent reviews covered core routing/configuration, Aspire lifecycle, and service ownership/failure handling.

Evidence retained locally:

- `.artifacts/verification/root-reviewed-01/`: orchestrator-run published Native AOT CLI, config precedence and clear, two callback listeners with exact raw queries, renewal/expiry/deletion, read-only doctor, artifact hash, and cleanup.
- `.artifacts/verification/aspire-package/`: packed NuGet consumption by real AppHosts, synthetic OAuth/code exchange, overlapping worktrees, paused API renewal, relay restart, explicit resource recovery, and cleanup. Final packed run passed 14 tests. Package consumption uses the repository build configuration; it is not an unrelated external consumer build.
- Discovery verification used the actual running Tailscale CLI and the local host's Tailscale address. Both relay and destination ran on the Windows development host. That proves local discovery and navigation through the Tailscale address, not reachability from an untested remote browser.

The solution builds with analyzers enabled. The standard test run deliberately skips the two integration tests when their run-owned relay prerequisites are absent; the dedicated packed run executes them. The generated verification skill has been executed, and cleanup preserves its evidence.

Outstanding delivery work: native CI execution for every RID, live Windows/systemd service verification, the full verification-map maintenance audit, final ticket acceptance records, and release handoff. No NuGet package or public release has been published.
