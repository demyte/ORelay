# 01: Run the native CLI and its agent feedback loop

Status: ready-for-agent

Blocked by: None.

Parent: [ORelay v1 specification](../spec.md#build-layout-and-distribution)

**What to build:** A developer can build and run the first native `orelay` executable, inspect help and version, and use working feedback commands plus a project verification skill to validate changes.

## Acceptance criteria

- [ ] Establish the .NET 10 executable and agreed project layout without adding unused application layers. Keep executable-only build settings separate from the future Aspire hosting library and tests.
- [ ] Add the explicitly requested `global.json`, `Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props` with a pinned SDK, central package versions, and purposeful shared settings. Add the MIT license.
- [ ] `orelay --help` and `orelay --version` work from the published executable, including when launched outside the checkout. Unknown commands and invalid arguments return actionable errors and nonzero exit codes.
- [ ] Help describes only implemented commands. It documents syntax and exit behavior; no normal invocation requires an interactive prompt. Help and version do not create config or other application state.
- [ ] Publish and execute a self-contained, single-file Native AOT binary for the implementing host. Resolve relevant AOT or trimming warnings instead of hiding them globally. Keep Windows, macOS, and Linux as required targets; cross-platform evidence arrives in ticket 14.
- [ ] Provide documented unattended formatting/lint, build, focused behavioral tests, and host-native AOT publish commands. Add an initial CI job that runs appropriate fast checks.
- [ ] Create and prove the project-local verification skill using `create-verification-skill`. Include the required launch, read-only health check, drive, evidence, and cleanup procedures and a feature map of actual commands. The health check can use implemented version/build behavior; do not pretend a `doctor` subcommand exists yet.
- [ ] Keep user setup and contribution instructions in the README, agent workflow references in AGENTS, and detailed verification recipes in the skill.

## Verification

Run the published binary's help, version, and invalid-input paths from a temporary working directory. Confirm expected exit codes and absence of configuration writes. Run the documented feedback commands, then follow the generated verification skill through one mapped feature and cleanup. Record commands, results, native artifact identity, and retained evidence. Do not claim other OS targets have passed.

## Scope

This slice ends with a useful CLI and feedback loop. Server routing, persistent configuration, service installation, and Aspire behavior are later tickets. Only expose implemented behavior.
