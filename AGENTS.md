# ORelay agent instructions

ORelay is a local-development OAuth 2 authorization callback relay for delegated API access. Keep the project small and provider-agnostic. Its job is to route callbacks to registered local worktrees. Provider grant and consent behavior, code exchange, token storage, and token refresh belong to the worktree or provider integration, not to the relay.

Load `C:\Users\james\.agents\skills\unslop\SKILL.md` before writing or revising project prose, and apply its plain-language guidance.

Treat the relay core, Aspire hosting integration, and operating-system packaging as separate concerns. Do not add a database or token store for the in-memory registration design. Default to a local-only listener and loopback callback destinations. Reject expired or unknown registration IDs without choosing a fallback destination.

Keep proposed behavior marked as planned until implementation and verification establish it. When a relay loses registrations, the consuming application must be explicitly restarted to receive a fresh registration ID; do not add automatic restarts or a dynamic application runtime package. Shared-relay authentication is deferred by James. Do not implement API keys, management secrets, or an authentication requirement in this version. Keep explicit binding and URL validation, and describe the actual trust model in user documentation.

Keep documentation separated by audience. `README.md` is for people learning about, installing, using, or contributing to ORelay. `AGENTS.md` contains only the instructions, constraints, and workflow references an agent needs to work in this repository. Keep verification-skill procedures and agent evidence rules out of the README; detailed verification recipes belong in the verification skill itself.

When implementation begins:

- Keep callback routing opaque. Preserve the complete callback query for the registered worktree, including state, code, error fields, and other provider values.
- Protect callback query strings, authorization codes, access tokens, refresh tokens, and secrets from logs, diagnostics, exceptions, and test output.
- Add meaningful focused tests for routing, concurrent registrations and callbacks, lease renewal and expiry, unknown registrations, and failure paths. Avoid tests that only mirror private implementation details.
- Make build, run, and test instructions match the actual repository. Do not document commands that have not been implemented.
- Keep changes within the relay scope unless James explicitly authorizes a broader feature or service installation.

## Agent feedback and verification

An agent must be able to make a change, run focused feedback commands, and prove the resulting user-facing behavior from this checkout. Build this capability into the first runnable increment and maintain it alongside the product.

- Provide documented commands for formatting and lint checks, build, focused tests, and Native AOT publishing. Keep routine checks fast and separate them from longer platform verification. Commands must run unattended, return meaningful exit codes, and report actionable failures.
- Run the checks appropriate to each change. A successful build does not establish that CLI commands, callback routing, services, or Aspire lifecycle behavior work.
- Generate `.agents/skills/verify-orelay/SKILL.md` and its `features/` map using the `create-verification-skill` workflow once the app builds and runs. Do not create a placeholder skill against nonexistent commands. Prove its launch, read-only doctor, one mapped feature, evidence capture, and cleanup before calling it usable.
- Keep the feature map current in the same change that adds or changes a user-facing command or behavior. Use `maintain-verification-skill` for a full audit: read each feature from source, exercise every mapped feature live, and report clean, changed, or blocked. A maintenance-only pass edits the verification skill and reports product regressions separately.
- Verification must drive the real CLI and HTTP behavior, plus a consuming AppHost when testing the Aspire package. Exercise concurrent worktrees, callback forwarding, lease lifecycle, configuration persistence and precedence, and platform service behavior as those features become available. Validate published Native AOT executables on the operating systems claimed as supported.
- Use a run-owned config file, ports, callback listeners, and processes. Before driving an instance, confirm its identity and readiness. Keep routine verification separate from the user's installed service, saved configuration, and live provider grants. Document any required platform, credentials, privileges, or external connection for a recipe.
- Capture commands, stdout/stderr, exit codes, HTTP results, and resulting file or registration state. Use synthetic OAuth values and sanitize evidence. Keep proof under `.artifacts/verification/<run-id>/` and temporary state under `work/verification/<run-id>/`.
- Clean up only resources created by the verification run, including after failures. Evidence must survive cleanup. State which features and platforms were exercised; record skipped or unreachable paths and their prerequisites without claiming they passed.

James authorized implementation of all v1 tickets with parallel subagents and reviewed incremental commits to main. The orchestrator owns integration, final verification, and commits; subagents own bounded file areas and do not stage, commit, or push. Tests must check meaningful external behavior and failure paths, not repeat the implementation.

Do not select a license, publish packages, or create external service configuration without an explicit request.

When creating or updating a GitHub issue, pull request description, or comment, include this footer once, using the actual model and reasoning level:

```md
> Written by Codex on behalf of James.
> _Codex [MODEL_SLUG] [REASONING_LEVEL]_
```

## Agent skills

### Issue tracker

Specs and tickets live as tracked Markdown files under `.scratch/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

Use a single root `CONTEXT.md` and `docs/adr/` when present. See `docs/agents/domain.md`.
