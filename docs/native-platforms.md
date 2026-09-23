# Native platform artifacts

ORelay ships one self-contained Native AOT executable for each supported runtime identifier (RID). A publish is valid only when it builds and runs on the target operating system. A Windows or WSL build cannot prove a Linux or macOS artifact.

## CI matrix

The original in-memory implementation passed all six OS and architecture pairs in the [native workflow on 2026-09-22](https://github.com/demyte/ORelay/actions/runs/35713463965), built from commit `5ffda0393eb41ab001ffffcbaacb9c56efdc26f9` on matching GitHub-hosted runners. That historical result does not establish SQLite or updater verification for a later checkout. The workflow now also exercises persistent registrations and builds SQLite for each target.

| Operating system | RID | GitHub runner | Native execution |
| --- | --- | --- | --- |
| Windows Server 2025 | `win-x64` | `windows-2025` | Passed, including SCM service lifecycle |
| Windows 11 ARM64 | `win-arm64` | `windows-11-arm` | Passed |
| Ubuntu 24.04 | `linux-x64` | `ubuntu-24.04` | Passed, including systemd service lifecycle |
| Ubuntu 24.04 ARM64 | `linux-arm64` | `ubuntu-24.04-arm` | Passed |
| macOS 15 Intel | `osx-x64` | `macos-15-intel` | Passed |
| macOS 14 Apple silicon | `osx-arm64` | `macos-14` | Passed |

Each original job proved that its executable starts and routes a callback while the selected .NET installation's shared runtime directory is unavailable. A managed control failed with the expected missing-runtime error during the same check. See [Releases](https://github.com/demyte/ORelay/releases) for published artifacts.

## Build prerequisites and baseline

The project pins the .NET SDK in `global.json`. Native publishing also needs PowerShell 7, the platform linker, and SDK:

- Windows needs Visual Studio 2022 or later with the Desktop development with C++ workload and the Windows SDK. The ARM64 row needs the ARM64 or ARM64EC C++ build tools.
- Linux needs `clang` and `zlib1g-dev`. The workflow installs these packages on each Ubuntu runner. Linux binaries built on Ubuntu 24.04 are verified on that image and should be treated as requiring Ubuntu 24.04 or a later compatible glibc environment until a wider runtime test is recorded.
- macOS needs the Xcode command-line tools. The macOS runner image supplies the SDK and linker. macOS direct execution is covered; no macOS service installer is implied.

The runner image is the verified build and execution baseline for each row. It is not evidence that an artifact runs on every older release of that operating system. Widen a minimum OS claim only after running the published binary on that OS and retaining the environment and executable evidence.

## Local publish

From the repository root, use a matching host and a temporary output directory:

```text
pwsh -NoProfile -File scripts/publish.ps1 -RuntimeIdentifier <rid> -OutputPath <publish-dir>
```

The executable name is `orelay.exe` on Windows and `orelay` elsewhere. A valid publish may include a separate symbol file, but it must not need `.dll`, `.so`, `.dylib`, `.deps.json`, `.runtimeconfig.json`, or runtime extraction files beside the executable. Keep `orelay.json` and its sibling `orelay.registrations.db` in a writable application-data location outside the publish directory.

`scripts/build-sqlite.ps1` downloads the pinned SQLite amalgamation, verifies both archive and C source hashes, and compiles a static library for the matching host. `publish.ps1` runs it before Native AOT publishing. Direct `dotnet publish` requires that library to have been built first. The native executable links SQLite directly; the dynamic library used by managed development builds is removed from Native AOT output. SQLite is public-domain software. Its version and source checksums are recorded in the build script.

## Native smoke path

The workflow first publishes the same project as a framework-dependent managed probe and proves that `dotnet <probe>.dll --version` succeeds. It then copies only the Native AOT executable into an empty execution directory, removes the discovered `dotnet` directory from `PATH`, points `DOTNET_ROOT` at an empty path, and temporarily renames the selected installation's validated `shared` runtime directory. A negative control must return the specific missing `Microsoft.NETCore.App` framework diagnostic before the native executable is exercised; the runtime directory is restored in `finally` and its move is restricted to the validated installation root. It checks `--help`, `--version`, and an invalid command, initializes a run-owned config with `init --config-file`, starts the foreground server on an owned loopback port, confirms the `/health` identity, registers a loopback callback destination, and follows a synthetic callback. The callback assertion compares the exact raw request target received by the destination with the query sent to `/callback`, including state, ordering, repeated and encoded values, and an empty value. The job retains the executable inventory, checksum, CLI output, HTTP results, and environment details as an artifact. It stops only the process started by that job.

This smoke path exercises the public executable and HTTP surface. The `win-x64` and `linux-x64` jobs also require a disposable SCM/systemd lifecycle proof. They initialize a selected config, refuse an unowned definition, install/start/status/restart/stop/uninstall an owned service, verify health and an exact callback through the service, verify unexpired registrations survive restart, and verify the config remains after uninstall. They also invoke the native installer against the owned service path while the service runs and while it is stopped. Paths include spaces and a literal dollar sign. Both jobs prove that an unprivileged service installation fails with `PermissionDenied` and exit code 3. Windows uses a temporary non-admin account and records its removal.

These service checks are mandatory for the x64 jobs. Missing privileges or systemd fail the job after recording the prerequisite. Native execution on ARM64 does not establish ARM64 service-manager coverage. The workflow does not exchange a real provider grant, publish a release, sign an executable, or claim a package-store submission.

## Artifacts and evidence

Each native job archives `orelay-<version>-<rid>.zip` on Windows or `orelay-<version>-<rid>.tar.gz` on Unix and writes a SHA-256 checksum file using the version and RID in every name. Unix archives use `tar` so extraction keeps the executable bit. The archive includes the executable, its optional symbols, and the MIT `LICENSE`. The job also uploads the sanitized smoke evidence separately. The [release workflow](releases.md) reuses these checks before publishing a version tag. Code signing and notarization remain separate work.

References: [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot), [Native AOT cross-compilation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile), and the [GitHub-hosted runners reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
