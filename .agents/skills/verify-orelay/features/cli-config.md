# CLI and saved configuration

## Sub-features

- Help, version, and invalid-input exit codes.
- `init` against a selected file.
- `config get`, `config set`, and `config clear` with JSON output.
- Default, saved, and invocation setting precedence.
- Read-only configuration checks through `doctor`.
- `setup` defaults, custom access, review, cancellation, and unattended use.

## How to get to it (user POV)

Use the executable from a terminal. Select a file with `--config-file`; a relative path is resolved from the current working directory, and no selected path means `orelay.json` beside the executable. `init` creates the file once. Use `config set` to persist later changes.

`setup` shows the selected settings and callback URL before it writes them. Use `setup --defaults --yes` for unattended local defaults, or supply `--yes` with explicit options such as `--access lan --hostname relay.test`. Both `--defaults` and `--if-needed` preserve an existing valid file. Service mode needs administrative privileges to install or update the selected service.

```powershell
orelay --config-file .run\orelay.json init --port 13871
orelay --config-file .run\orelay.json config get --json
orelay --config-file .run\orelay.json config set leaseSeconds 300 --json
orelay --config-file .run\orelay.json config clear port --json
orelay --config-file .run\setup.json setup --defaults --yes --json
```

## Driving it with PowerShell

Run `.agents/skills/verify-orelay/scripts/verify.ps1`. It first checks that a fresh `init` writes exactly the default configuration, including `hostname: localhost`, and retains that file as evidence. It then uses a new configuration under `work/verification/<run-id>`, checks saved JSON and SHA-256 state, starts a server with an invocation port override, and confirms that the override does not rewrite the saved file. It clears and restores `port` and checks the built-in default. Doctor uses a unique service name so an unrelated installed ORelay service cannot affect this foreground check.

For native setup, publish the executable on a matching host and run `.github/workflows/setup-smoke.ps1 -ExecutablePath <absolute-executable-path> -RunRoot <run-owned-directory> -Rid <rid>`. It keeps configuration under `<run-owned-directory>/setup-smoke-state` and exit codes plus separate stdout/stderr under `<run-owned-directory>/evidence`. It checks defaults, existing-file preservation, custom LAN settings, and rejected redirected or invalid commands. It deletes only its own state directory. `.github/workflows/native-platforms.yml` runs it for all six published RIDs. These unattended checks do not prove terminal prompts, Tailscale connectivity, or service-manager installation. Focused `SetupCommandTests` drive those decisions with injected input and fake service or discovery results.

## Gotchas

- `config get` does not create a missing file.
- `init` preserves an existing valid file, even when supplied overrides differ.
- Malformed files must remain intact on failed reads and doctor checks.
- JSON goes to stdout while diagnostics go to stderr. Preserve both streams in evidence.
- `doctor --fix` writes a missing file. Treat it as a separate write path.
- New local configurations advertise `http://localhost:<port>/callback` while binding to `127.0.0.1`. A saved hostname remains an explicit override. To use Tailscale discovery with a previously initialized config, set `autoDiscovery` to `tailscale` and clear `hostname`.
- Redirected `setup` needs `--yes`; without it, the selected file stays unchanged.
