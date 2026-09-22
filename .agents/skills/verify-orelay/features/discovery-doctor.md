# Discovery and doctor

## Sub-features

- Resolve local or explicitly configured callback addresses.
- Report Tailscale discovery prerequisites without guessing a destination.
- Check a selected configuration and intended listener.
- Probe relay identity through `/health`.
- Apply a bounded missing-file repair with `doctor --fix`.

## How to get to it (user POV)

Use `publicUrl`, `hostname`, and `autoDiscovery` in the selected config. Run doctor to inspect the file, intended listener, advertised callback, health, port, discovery, and service checks that are available on the current platform.

```powershell
orelay --config-file .run\orelay.json config set autoDiscovery none --json
orelay --config-file .run\orelay.json doctor --json
orelay --config-file .run\missing.json doctor --json
orelay --config-file .run\missing.json doctor --fix --json
```

## Driving it with PowerShell

The verification helper runs doctor against a live relay and compares the configuration hash before and after. It also runs read-only doctor against a missing file and asserts exit code `1` plus no file creation. It does not claim a Tailscale pass because that requires the local daemon and a browser-reachable candidate. Use `src/ORelay/Discovery/CallbackDiscovery.cs`, `RelaySettingsDiscovery.cs`, `DiscoveryJson.cs`, and `TailscaleStatusProcessProvider.cs` as the source contract for a focused discovery run.

For shared proof, choose run-owned ports on an available Tailscale interface, launch with `--auto-discovery tailscale`, and run doctor before registering a destination. Follow the redirect from the declared browser host and compare the destination's raw request target. Record that host's network position. If it cannot resolve the discovered DNS name, retain that failure and rerun with an explicit reachable `--hostname` IP; do not change DNS or firewall settings as a repair.

To verify occupied-port diagnosis, bind a run-owned foreign HTTP listener and return a non-ORelay identity from `/health`. Point a disposable saved config at that port and invoke the native `doctor --json`. Expect exit 1, failed management identity and occupied-port checks, an unchanged config hash, and a listener that remains running until the verifier closes it. A fake health probe alone does not prove this path.

## Gotchas

- Read-only doctor must not create, repair, or rewrite a selected configuration.
- A successful relay health probe proves relay identity only. It does not prove callback reachability from a browser or worktree host.
- `doctor --fix` is a write operation and must use disposable run-owned state.
- Missing Tailscale, ambiguous status, and incompatible bindings are diagnostic outcomes. They are not permission to select a guessed hostname.
