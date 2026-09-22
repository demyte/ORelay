# Native platform artifacts

ORelay ships one self-contained Native AOT executable for each supported runtime identifier (RID). A publish is valid only when it builds and runs on the target operating system. A Windows or WSL build cannot prove a Linux or macOS artifact.

## CI matrix

The native workflow uses the matching GitHub-hosted runner for all six proposed OS and architecture pairs. These labels are available to private repositories according to the GitHub-hosted runner reference:

| Operating system | RID | GitHub runner | Native execution |
| --- | --- | --- | --- |
| Windows Server 2025 | `win-x64` | `windows-2025` | Required in CI |
| Windows 11 ARM64 | `win-arm64` | `windows-11-arm` | Required in CI |
| Ubuntu 24.04 | `linux-x64` | `ubuntu-24.04` | Required in CI |
| Ubuntu 24.04 ARM64 | `linux-arm64` | `ubuntu-24.04-arm` | Required in CI |
| macOS 15 Intel | `osx-x64` | `macos-15-intel` | Required in CI |
| macOS 14 Apple silicon | `osx-arm64` | `macos-14` | Required in CI |

The repository currently has no self-hosted GitHub Actions runners. The local Windows machine can inspect and publish `win-x64` after the Visual Studio C++ toolchain is loaded, but it does not have the Windows ARM64 C++ toolchain. WSL2 is Ubuntu 24.04 x64 without the clang and zlib development packages required by Native AOT. Neither local result is release evidence for another operating system or architecture. CI runs are the first place where this matrix can become supported.

Until every matrix row has a successful native execution record, the row is unverified and must not be described as a released target. A failed or unavailable hosted runner is a coverage blocker. Do not remove the row or mark it successful from a cross-publish.

## Build prerequisites and baseline

The project pins the .NET SDK in `global.json`. Native publishing also needs the platform linker and SDK:

- Windows needs Visual Studio 2022 or later with the Desktop development with C++ workload and the Windows SDK. The ARM64 row needs the ARM64 or ARM64EC C++ build tools.
- Linux needs `clang` and `zlib1g-dev`. The workflow installs these packages on each Ubuntu runner. Linux binaries built on Ubuntu 24.04 are verified on that image and should be treated as requiring Ubuntu 24.04 or a later compatible glibc environment until a wider runtime test is recorded.
- macOS needs the Xcode command-line tools. The macOS runner image supplies the SDK and linker. macOS direct execution is covered; no macOS service installer is implied.

The runner image is the verified build and execution baseline for each row. It is not evidence that an artifact runs on every older release of that operating system. Widen a minimum OS claim only after running the published binary on that OS and retaining the environment and executable evidence.

## Local publish

From the repository root, use a matching host and a temporary output directory:

```text
dotnet publish src/ORelay/ORelay.csproj --configuration Release --runtime <rid> --self-contained true --output <publish-dir> -p:PublishAot=true -p:PublishSingleFile=true
```

The executable name is `orelay.exe` on Windows and `orelay` elsewhere. A valid publish may include a separate symbol file, but it must not need `.dll`, `.deps.json`, `.runtimeconfig.json`, or runtime extraction files beside the executable. Keep `orelay.json` in a writable application-data location outside the publish directory.

## Native smoke path

The workflow first publishes the same project as a framework-dependent managed probe and proves that `dotnet <probe>.dll --version` succeeds. It then copies only the Native AOT executable into an empty execution directory, removes the discovered `dotnet` directory from `PATH`, points `DOTNET_ROOT` at an empty path, and temporarily renames the selected installation's validated `shared` runtime directory. A negative control must return the specific missing `Microsoft.NETCore.App` framework diagnostic before the native executable is exercised; the runtime directory is restored in `finally` and its move is restricted to the validated installation root. It checks `--help`, `--version`, and an invalid command, initializes a run-owned config with `init --config-file`, starts the foreground server on an owned loopback port, confirms the `/health` identity, registers a loopback callback destination, and follows a synthetic callback. The callback assertion compares the exact raw request target received by the destination with the query sent to `/callback`, including state, ordering, repeated and encoded values, and an empty value. The job retains the executable inventory, checksum, CLI output, HTTP results, and environment details as an artifact. It stops only the process started by that job.

This smoke path exercises the public executable and HTTP surface. The `win-x64` and `linux-x64` jobs additionally run a disposable service lifecycle proof when the hosted runner exposes the required SCM/systemd privilege: initialize a selected config, refuse an unowned definition, install/start/status/restart/stop/uninstall an owned service, verify health and an exact callback through the service, verify registrations are lost across restart, and verify the config remains after uninstall. A runner without the required service-manager privilege records an explicit skipped-evidence result; that does not make the service row a release claim. The workflow does not exchange a real provider grant, publish a release, sign an executable, or claim a package-store submission.

## Artifacts and evidence

Each native job archives `orelay-<rid>.zip` on Windows or `orelay-<rid>.tar.gz` on Unix and writes a SHA-256 checksum file using the RID in every name. Unix archives use `tar` so extraction keeps the executable bit. The archive includes the executable, its optional symbols, and the MIT `LICENSE`. The job also uploads the sanitized smoke evidence separately. Release publication, code signing, notarization, and package uploads remain explicit later actions.

References: [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot), [Native AOT cross-compilation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile), and the [GitHub-hosted runners reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
