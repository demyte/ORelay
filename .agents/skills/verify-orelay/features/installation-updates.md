# Installation and self-updates

## Source and commands

`install.ps1` and `install.sh` select a native release, verify its checksum, and invoke the downloaded executable's `install` command. `src/ORelay/Updating` owns release discovery, downloads, extraction, executable replacement, and rollback. `CliParser` exposes `install`, `update`, `--check`, and the explicit service restart flags.

```text
orelay install --install-dir <run-owned-directory> --json
orelay update --check --json
orelay --config-file <run-owned-config> update --restart-service --name <run-owned-service> --json
```

Use the published Native AOT executable. Managed execution must return exit code 69 for installation and update attempts. Public release downloads and a read-only update check need GitHub network access, with no credentials required. Capture its result without printing any optional tokens. It does not establish replacement or rollback.

## Focused feedback

```powershell
dotnet test tests/ORelay.Tests --filter FullyQualifiedName~Updating
powershell.exe -NoProfile -File scripts/test-bootstrap.ps1
```

Run the Windows fixture with Windows PowerShell 5.1, which compiles its small fixture executable. On Unix, run `sh scripts/test-bootstrap.sh`. These fixtures control downloads and destinations; they must reject corrupt checksums and missing assets before executing a downloaded candidate.

## Native proof

Install the current executable into a fresh run-owned directory. Check `--version --json`, compare source and installed SHA-256, and repeat installation to prove the no-change result. Keep a config and database beside the target and verify their bytes survive replacement. Test directory names containing spaces. Verify an invalid target fails without overwriting unrelated files.

Exercise self-replacement with two published fixture versions, a controlled release response, and recorded commands/exit codes. Check that a mismatched checksum, wrong version, unsafe archive, or failed restart leaves the old executable usable. Never publish verification tags or replace real release assets to construct this fixture. Focused tests cover injected network and service failures; they do not prove native replacement alone.

For Windows and Linux services, `.github/workflows/service-smoke.ps1` uses an owned service name and paths. It preserves unexpired callbacks across restart and installation. Same-version installation proves idempotence and state preservation, not a transition between different executable bytes. Service replacement needs administrative privileges on a disposable host. macOS has no service manager integration in this version.

Store commands, outputs, HTTP results, hashes, versions, and service state under `.artifacts/verification/<run-id>/`. Keep temporary installs under `work/verification/<run-id>/`, clean only owned resources, and retain evidence after cleanup. Report each platform and operation actually exercised; do not infer Linux or macOS success from Windows results.
