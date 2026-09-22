# ORelay verification map

The map follows the user-visible surfaces in the current checkout. The PowerShell helper covers the local Native AOT CLI and HTTP relay paths. Aspire and service-manager paths have separate prerequisites and are recorded without claiming a local pass.

| Feature | User entry point | Proof file |
| --- | --- | --- |
| CLI and saved configuration | `orelay init` and `orelay config` | [cli-config.md](cli-config.md) |
| Callback relay and leases | `orelay server` plus `/registrations` and `/callback` | [relay-leases.md](relay-leases.md) |
| Discovery and doctor | `orelay doctor` and `autoDiscovery` settings | [discovery-doctor.md](discovery-doctor.md) |
| Aspire hosting package | `WithORelay` in a consuming AppHost | [aspire.md](aspire.md) |
| Native artifacts and services | `scripts/publish.ps1`, `orelay service` | [services-native.md](services-native.md) |

The map names only commands and files present in this checkout. A feature is clean only after the real path and its side effects are checked. A skipped external tool or platform is recorded as blocked or unverified.
