# Versions and releases

ORelay and `ORelay.Aspire.Hosting` share one version derived from Git tags by [MinVer](https://github.com/adamralph/minver). Choose the version when tagging; commit messages do not bump it.

| Source | Package and download version | CLI and product version |
| --- | --- | --- |
| `v0.2.0` | `0.2.0` | `0.2.0+<commit>` |
| `v0.2.0-rc.1` | `0.2.0-rc.1` | `0.2.0-rc.1+<commit>` |
| Commits after `v0.2.0` | `0.2.1-dev.0.<height>` | The same version plus `<commit>` |

Before the first tag, development builds use `0.1.0-dev.0.<height>`. The assembly and file versions use the numeric `MAJOR.MINOR.PATCH.0` parts. Prerelease identifiers and the source commit are retained in the product/informational version, including Native AOT executables. `orelay --version` and `orelay --version --json` read this build stamp.

Run `pwsh -NoProfile -File scripts/get-version.ps1` to inspect the version before building. Clone with full history and tags. A shallow checkout cannot reliably calculate a development version. Uncommitted edits do not change the Git version; use a clean, committed checkout for distributable builds.

## Create a release

Push a version tag on a reviewed commit already in `main`:

```powershell
git switch main
git pull --ff-only
git tag -a v0.2.0 -m "ORelay 0.2.0"
git push origin v0.2.0
```

Use the version you intend to publish. `v0.2.0` above is an example, not a reserved next version. A tag such as `v0.2.0-rc.1` produces a GitHub prerelease and a NuGet prerelease package.

The release workflow validates the tag, runs formatting/build/tests, builds and exercises all six native platforms, and tests consumption of the actual NuGet package. Only after those checks pass does it publish the package and finish the GitHub release. Release downloads include versioned ZIP or tar.gz archives, checksums, and the hosting `.nupkg`.

Release tags must start with lowercase `v` and contain valid SemVer without `+build` metadata. Numeric parts cannot exceed 65534, the .NET assembly version limit. Tags must identify the checked-out commit and that commit must belong to `origin/main`. Multiple version tags on one commit are allowed only when the tag being released matches the version selected by MinVer. Do not move a published tag or reuse a published package version.

The workflow creates a draft release before publishing the package, then makes the release visible after package publication succeeds. If a run fails, inspect its logs before retrying. A published package cannot be rolled back as part of the release transaction. Fix changed source with a new version tag.

Use **Re-run failed jobs** to reuse the verified artifacts after a publication failure. The workflow keeps matching draft assets and packages, and rejects any with different bytes. A full rebuild can change archive bytes, so it may require a new version rather than replacing an existing upload. A finalized release is never overwritten.

Ordinary pushes to `main` and pull requests only produce build artifacts. They never publish a package or release. Setting up this workflow does not create the first tag.

## Bootstrap and self-update releases

The root `install.ps1` and `install.sh` scripts select the platform archive and its checksum from the latest stable release, then invoke the downloaded executable's `install` command. The selected release must include that command; versions through `v0.1.2` predate it. Keep the archive naming contract `orelay-<version>-<rid>.zip` or `.tar.gz`, with a matching `.sha256` file.

`orelay update` uses the same release assets. It excludes drafts and prereleases and refuses automatic downgrades. Native artifacts contain SQLite linked into the executable. Keep configuration and the registration database outside executable replacement, and retain database compatibility with the previous binary so rollback can restart it.

Releasing these changes still requires an explicitly authorized version tag. Adding bootstrap scripts or updating README examples does not publish a release.

## GitHub Packages

The feed is `https://nuget.pkg.github.com/demyte/index.json`. The package is associated with `demyte/ORelay` through its repository metadata. The publish job receives `packages: write` and `contents: write`; other jobs have read-only repository access. It uses the workflow's `GITHUB_TOKEN`, so no publishing PAT is required.

The repository and `ORelay.Aspire.Hosting` package are public. GitHub's NuGet registry still requires authentication to install public packages. To consume the package locally, use a GitHub personal access token, classic, with `read:packages`. Keep credentials in your user-level NuGet configuration or environment, outside the repository. GitHub's [NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-to-github-packages) describes this requirement. Native archives and the `.nupkg` attached to a public GitHub release can be downloaded without credentials.

For a consuming repository, add a source without credentials:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="demyte" value="https://nuget.pkg.github.com/demyte/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="demyte"><package pattern="ORelay.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Supply the `demyte` source credentials through the NuGet environment variable `NuGetPackageSourceCredentials_demyte`, with value `Username=<github-user>;Password=<token>;ValidAuthenticationTypes=Basic`. Do not commit the token or paste it into build logs. In another repository's GitHub Actions workflow, grant that repository read access in the package settings and use its `GITHUB_TOKEN` with `packages: read`, or supply a classic PAT with package read access when that grant is unavailable.

Then reference the published version from the AppHost:

```powershell
dotnet add path/to/AppHost.csproj package ORelay.Aspire.Hosting --version 0.2.0
```

Use central package management instead when the consumer uses `Directory.Packages.props`. The API itself does not reference the hosting package. xpnAI's local DLL integration can be migrated separately after the first package is published.

## Remaining distribution options

Windows Authenticode signing, macOS signing/notarization, and package-manager installers are separate distribution work. These builds are unsigned. They are not prerequisites for version stamping, GitHub releases, or NuGet publication.
