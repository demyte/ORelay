# CLI and saved configuration

## Sub-features

- Help, version, and invalid-input exit codes.
- `init` against a selected file.
- `config get`, `config set`, and `config clear` with JSON output.
- Default, saved, and invocation setting precedence.
- Opt-in service updates through `autoUpdate` and `autoUpdateIntervalSeconds`.
- Read-only configuration checks through `doctor`.
- `setup` defaults, custom access, review, cancellation, unattended use, and optional user PATH.

## How to get to it (user POV)

Use the executable from a terminal. Select a file with `--config-file`; a relative path is resolved from the current working directory, and no selected path means `orelay.json` beside the executable. `init` creates the file once. Use `config set` to persist later changes.

`setup` shows the selected settings and callback URL before it writes them. Use `setup --defaults --yes` for unattended local defaults, or supply `--yes` with explicit options such as `--access lan --hostname relay.test`. Both `--defaults` and `--if-needed` preserve an existing valid file. Service mode needs administrative privileges to install or update the selected service.

Interactive setup offers to add the executable directory to user PATH and includes that change in the confirmation. `--skip-path` suppresses the offer. `--yes` changes PATH only with explicit `--add-to-path`. An existing config can be preserved while PATH is updated. `src/ORelay/Setup/UserPathManager.cs` handles the Windows user PATH and sh/bash/zsh/fish startup files. Run `dotnet test tests/ORelay.Tests --filter FullyQualifiedName~Setup` for cancellation, explicit consent, preservation, idempotence, shell quoting, and injected write failures. Unit tests use temporary profiles and fake Windows environment callbacks.

```powershell
orelay --config-file .run\orelay.json init --port 13871
orelay --config-file .run\orelay.json config get --json
orelay --config-file .run\orelay.json config set leaseSeconds 300 --json
orelay --config-file .run\orelay.json config clear port --json
orelay --config-file .run\setup.json setup --defaults --yes --json
```

## Driving it with PowerShell

Run `.agents/skills/verify-orelay/scripts/verify.ps1`. It first checks that a fresh `init` writes exactly the default configuration, including `hostname: localhost`, and retains that file as evidence. It then uses a new configuration under `work/verification/<run-id>`, checks saved JSON and SHA-256 state, starts a server with an invocation port override, and confirms that the override does not rewrite the saved file. It clears and restores `port` and checks the built-in default. Doctor uses a unique service name so an unrelated installed ORelay service cannot affect this foreground check.

The helper also checks default `autoUpdate: false` and `autoUpdateIntervalSeconds: 86400`, saves an explicit opt-in with a 60-second interval, and rejects 59 seconds without changing the saved value. Pass `-CheckAutoUpdate` to keep its foreground server alive beyond that interval and confirm no automatic worker result appears while health and callback routing remain available. Focused configuration tests cover clearing both settings, missing fields in older configurations, malformed/null values, and preservation during unrelated edits. The service-manager proof is in [installation and updates](installation-updates.md).

For native setup, publish the executable on a matching host and run `.github/workflows/setup-smoke.ps1 -ExecutablePath <absolute-executable-path> -RunRoot <run-owned-directory> -Rid <rid>`. It keeps configuration under `<run-owned-directory>/setup-smoke-state` and exit codes plus separate stdout/stderr under `<run-owned-directory>/evidence`. It checks defaults, existing-file preservation, custom LAN settings, and rejected redirected or invalid commands. It deletes only its own state directory. `.github/workflows/native-platforms.yml` runs it for all six published RIDs. These unattended checks do not prove terminal prompts, Tailscale connectivity, or service-manager installation. Focused `SetupCommandTests` drive those decisions with injected input and fake service or discovery results.

## Gotchas

- Never run `setup --add-to-path` against the user's real environment as routine verification. `.github/workflows/user-path-smoke.ps1` uses run-owned Unix profiles. Its Windows path requires `-AllowWindowsUserPath` and runs only on disposable CI hosts, where it restores the original user PATH. The native workflow exercises the published executable, repeats setup without rewriting config or PATH, and resolves `orelay --version` in a fresh process. A local Windows unit test is not proof of registry mutation.

- `config get` does not create a missing file.
- `init` preserves an existing valid file, even when supplied overrides differ.
- Malformed files must remain intact on failed reads and doctor checks.
- JSON goes to stdout while diagnostics go to stderr. Preserve both streams in evidence.
- `doctor --fix` writes a missing file. Treat it as a separate write path.
- New local configurations advertise `http://localhost:<port>/callback` while binding to `127.0.0.1`. A saved hostname remains an explicit override. To use Tailscale discovery with a previously initialized config, set `autoDiscovery` to `tailscale` and clear `hostname`.
- Redirected `setup` needs `--yes`; without it, the selected file stays unchanged.
