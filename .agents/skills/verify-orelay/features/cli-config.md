# CLI and saved configuration

## Sub-features

- Help, version, and invalid-input exit codes.
- `init` against a selected file.
- `config get`, `config set`, and `config clear` with JSON output.
- Default, saved, and invocation setting precedence.
- Read-only configuration checks through `doctor`.

## How to get to it (user POV)

Use the executable from a terminal. Select a file with `--config-file`; a relative path is resolved from the current working directory, and no selected path means `orelay.json` beside the executable. `init` creates the file once. Use `config set` to persist later changes.

```powershell
orelay --config-file .run\orelay.json init --port 13871
orelay --config-file .run\orelay.json config get --json
orelay --config-file .run\orelay.json config set leaseSeconds 300 --json
orelay --config-file .run\orelay.json config clear port --json
```

## Driving it with PowerShell

Run `.agents/skills/verify-orelay/scripts/verify.ps1`. It first checks that a fresh `init` writes exactly the default configuration, including `hostname: localhost`, and retains that file as evidence. It then uses a new configuration under `work/verification/<run-id>`, checks saved JSON and SHA-256 state, starts a server with an invocation port override, and confirms that the override does not rewrite the saved file. It clears and restores `port` and checks the built-in default. Doctor uses a unique service name so an unrelated installed ORelay service cannot affect this foreground check.

## Gotchas

- `config get` does not create a missing file.
- `init` preserves an existing valid file, even when supplied overrides differ.
- Malformed files must remain intact on failed reads and doctor checks.
- JSON goes to stdout while diagnostics go to stderr. Preserve both streams in evidence.
- `doctor --fix` writes a missing file. Treat it as a separate write path.
- New local configurations advertise `http://localhost:<port>/callback` while binding to `127.0.0.1`. A saved hostname remains an explicit override. To use Tailscale discovery with a previously initialized config, set `autoDiscovery` to `tailscale` and clear `hostname`.
