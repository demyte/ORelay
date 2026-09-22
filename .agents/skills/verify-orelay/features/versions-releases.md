# Version stamps and releases

## Source and user paths

`Directory.Build.props` configures MinVer, `Directory.Build.targets` exposes calculated stamps, and `CliApplication.Version` reads the built informational version. `scripts/get-version.ps1` reports JSON and validates release tags when passed `-Tag`. `.github/workflows/release.yml` gates GitHub releases and NuGet publication behind CI, native execution, and package-consumer tests.

## Local proof without publishing

```powershell
pwsh -NoProfile -File scripts/get-version.ps1
pwsh -NoProfile -File scripts/test-versioning.ps1
dotnet pack src/ORelay.Aspire.Hosting -c Release -o .artifacts/packages/version-check
pwsh -NoProfile -File scripts/verify-package.ps1 -PackageDirectory .artifacts/packages/version-check -ExpectedVersion <calculated-version>
```

The versioning check copies the current source into a disposable Git repository under `work/verification`. It builds the real CLI and package at prerelease and stable tags, verifies the CLI against assembly/file/product stamps, then checks the next development version. It covers annotated and lightweight tags, rejects invalid tags, a dirty checkout, a tag on the wrong commit, and a commit outside `origin/main`. It writes build and version evidence to `.artifacts/verification/versioning-<id>` and deletes only its validated scratch directory. It never tags or pushes the actual repository.

The package check extracts the actual `.nupkg`, checks its identity, version, repository commit, license, and DLL stamps, and builds/tests the AppHost consumer against an isolated NuGet cache. Its restore uses source mapping to ensure it consumes the local artifact. It does not claim the gated Aspire integration tests ran. When an Aspire lifecycle change also needs verification, follow [aspire.md](aspire.md).

Publish the current Windows executable with `scripts/publish.ps1` and compare `orelay --version --json` with `scripts/get-version.ps1`. On Windows, inspect `FileVersionInfo` for the native executable. The native CI workflow performs version checks on every RID and retains the output with its existing execution evidence.

## External proof

Creating a real version tag starts publication. Do this only when the user requests a release of that version. After a release, confirm all six native jobs, inspect uploaded checksums, download the package from `https://nuget.pkg.github.com/demyte/index.json`, and build a consumer with the required feed credentials. If no release was requested, report publication as unverified instead of creating a test release.

See `docs/releases.md` for the human release and private-feed instructions. Signing/notarization and package visibility changes are outside this workflow.
