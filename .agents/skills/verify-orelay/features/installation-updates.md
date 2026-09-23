# Installation and self-updates

## Source and commands

`install.ps1` and `install.sh` select a native release, verify its checksum, and invoke the downloaded executable's `install` command. After a successful install, they run `setup --if-needed` from the installed executable when a console is available. `-Defaults` or `--defaults` runs setup unattended with `--defaults --yes`; `-SkipSetup` or `--skip-setup` prints the installed path and setup arguments for later use. The two flags cannot be combined. `src/ORelay/Updating` owns release discovery, downloads, extraction, executable replacement, and rollback. `CliParser` exposes `install`, `setup`, `update`, `--check`, and the explicit service restart flags.

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

Run the Windows fixture with Windows PowerShell 5.1, which compiles its small fixture executable. On Unix, run `sh scripts/test-bootstrap.sh`. These fixtures control downloads and destinations; they must reject corrupt checksums and missing assets before executing a downloaded candidate. They also check that setup runs from the installed path, keeps config and service arguments, skips without a terminal, honors unattended defaults and skip flags, and passes through install or setup failures. For a live interactive check, pipe the installer source into a shell with a real terminal. Confirm that setup reads from the terminal rather than consuming the piped source. Use only a run-owned install directory and config.

Both fixtures check `-AddToPath`/`--add-to-path` and `-SkipPath`/`--skip-path` forwarding, reject conflicting PATH flags or PATH flags with skip-setup, and reject explicit unattended PATH additions without defaults before installation. The native [CLI configuration proof](cli-config.md) separately verifies persistent PATH behavior. Bootstrap fixtures must not write the real user's PATH or profiles.

`UpdateEngineTests.Replacement_StopStatusFailure*` covers both install and update when a running service stops but the following status check fails. The original executable must remain intact, and recovery must restart the service and verify health. If recovery fails, the result must explicitly report that failure. The denied-stop test also proves that a service which remains running receives no start command. These tests inject service failures and do not claim native service-manager coverage.

`UpdateValidationTests` rejects oversized TAR sidecars and compares numeric prerelease identifiers beyond machine-integer limits. Unix executable probes verify normal version output, cancellation, and termination when either stdout or stderr exceeds its 1,024-character budget. Windows reports these script-based cases as skipped; native installation exercises successful version probes on every platform.

`UpdateEngineTests` verifies that same-version installation skips only byte-identical artifacts and replaces differing builds. Unix link cases verify rejection before creating directories or lock files, including a dangling lock-file symlink. Windows junction rejection can be checked with a run-owned junction and published executable; no child destination or lock may appear in the linked directory.

`InstalledExecutableMetadata` reads existing binaries without executing them. Its bounded reader recognizes the Native AOT Company/FileVersion/InformationalVersion/RepositoryUrl metadata frame found in the supported binaries, checks the native header and version agreement, and rejects missing or ambiguous frames. This compiler layout is not a public SDK contract. Recheck the published artifacts on every RID after SDK or version-stamping changes; do not introduce an execution fallback. Candidate probes run only after download checksum or source-copy verification. `InstalledExecutableMetadataTests` covers corrupt and ambiguous metadata, length bounds, and buffer boundaries. The installer regression also uses a Unix script that would write a marker if probed and verifies it never runs.

## Native proof

Install the current executable into a fresh run-owned directory. Check `--version --json`, compare source and installed SHA-256, and repeat installation to prove the no-change result. Keep a config and database beside the target and verify their bytes survive replacement. Test directory names containing spaces. Verify an invalid target fails without overwriting unrelated files.

Run `.github/workflows/install-safety-smoke.ps1 -ExecutablePath <published-executable> -RunRoot <run-owned-directory> -Rid <rid>`. It creates and checks a harmless marker-writing executable, then proves native installation rejects that unrelated destination without executing it or changing its hash. Windows compiles only this authored fixture with Windows PowerShell 5.1; Unix uses an authored shell script. The native workflow runs this check on all six RIDs.

Exercise self-replacement with two published fixture versions, a controlled release response, and recorded commands/exit codes. Check that a mismatched checksum, wrong version, unsafe archive, or failed restart leaves the old executable usable. Never publish verification tags or replace real release assets to construct this fixture. Focused tests cover injected network and service failures; they do not prove native replacement alone.

For Windows x64 and Linux x64 services, `.github/workflows/native-platforms.yml` builds three Native AOT fixtures from a temporary source copy with version overrides. Only the failing fixture's temporary `Program.cs` exits when started as a server. The production artifact stays in its own publish directory. `.github/workflows/service-smoke.ps1` installs the older fixture, replaces it with production while running, replaces production with the newer fixture while stopped, then attempts the failing candidate while running. The script requires `changed=true` on successful replacements, checks versions and hashes, confirms the original stopped state, and proves rollback restored the previous executable and running service. It checks the same callback after each restart, plus the selected configuration and registration database. These privileged service checks run only on disposable CI hosts. macOS has no service manager integration in this version.

Store commands, outputs, HTTP results, hashes, versions, and service state under `.artifacts/verification/<run-id>/`. Keep temporary installs under `work/verification/<run-id>/`, clean only owned resources, and retain evidence after cleanup. Report each platform and operation actually exercised; do not infer Linux or macOS success from Windows results.
