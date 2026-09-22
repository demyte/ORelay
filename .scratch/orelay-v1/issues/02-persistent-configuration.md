# 02: Initialize, inspect, and edit persistent configuration

Status: ready-for-agent

Blocked by: [01: Run the native CLI and its agent feedback loop](01-native-cli-and-feedback.md).

Parent: [ORelay v1 specification](../spec.md#cli-and-configuration)

**What to build:** An operator can create, inspect, and change ORelay settings through the CLI, select a persistent config location, and diagnose configuration without modifying it.

## Acceptance criteria

- [ ] Use `orelay.json` beside the executable by default, independent of the current working directory. A global `--config-file` selects another path; document how relative paths resolve.
- [ ] `init` creates a missing file using built-in defaults plus supplied setting overrides. Repeating it validates and preserves the existing file, even when new overrides were supplied. Document how to persist changes instead.
- [ ] `config get [key]` reports effective settings and defaults without creating a missing file. `config set <key> <value>` validates and persists the value while preserving unrelated valid settings.
- [ ] `config clear <key>` removes the saved override so the built-in default applies. Clearing an already-default known key is harmless; unknown keys remain errors.
- [ ] Define the initial settings schema, defaults, value types, and validation. Resolve effective values as defaults, saved values, then invocation overrides. An invocation override does not rewrite an existing file.
- [ ] Invalid JSON, unknown keys, invalid values, and unsupported config versions produce actionable errors with the selected path or setting. Malformed files remain intact.
- [ ] Writes are atomic. Concurrent writers either preserve both updates or return a clear conflict; they cannot silently lose unrelated updates. A write or permission failure leaves the old file intact and never switches to another location silently.
- [ ] A config-focused `doctor` checks without writes. Help and version remain free of config side effects. Full repair and connectivity diagnostics arrive in ticket 11.
- [ ] Document command syntax, examples, defaults, and exit codes. Data-returning commands support machine-readable JSON with diagnostics separate from structured stdout. Extend the verification map with the implemented config behavior.

## Verification

Drive the real CLI against isolated missing, valid, and malformed files. Change the working directory and use an alternate config path. Compare effective values, saved contents, exit codes, and file hashes before and after read-only or failed operations. Exercise clear-to-default, repeated init, concurrent updates, and an unwritable location. Run focused checks and native publishing appropriate to the change, then retain evidence after cleanup.

## Scope

The server will consume these rules in ticket 03. Live config reload, environment-variable precedence, service installation, and silent repair of invalid files are excluded.
