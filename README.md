# ubit

> **Codename.** "ubit" is a working name only. The public product name will be chosen
> after a trademark review.

ubit is a privacy-first Linux desktop (Ubuntu 26.04 LTS + KDE Plasma) that people can
try inside Windows or macOS, see how ready they are to leave Big Tech, and then switch.

This repository holds the **v0 walking skeleton**: the image, a guest agent, the app
catalog, and two thin native host apps.

```
image/           mkosi build: base image + per-host profiles (mac arm64, windows x86_64)
agent/           Rust workspace: protocol, scoring, ubit-agent daemon, ubit-hostsim
catalog/         YAML: which host apps map to which Linux replacements
protocol/        JSON Schema + fixtures for the host <-> agent protocol (source of truth)
hosts/macos/     Swift app on Virtualization.framework (Apple Silicon, macOS 14+)
hosts/windows/   C# / WPF app: Hyper-V on Windows 11 Pro/Enterprise/Education,
                 QEMU + Windows Hypervisor Platform on Windows 11 Home
docs/            architecture notes and threat model
```

## How the pieces fit

```
 macOS app (Swift)          Windows app (C#)
 Virtualization.framework   Hyper-V (Pro)          or  QEMU + WHPX (Home)
        |  vsock 7710            |  hv_sock -> vsock 7710     |  virtio-serial
        +------------------------+----------------------------+
                      v
        Guest: Ubuntu 26.04 + KDE Plasma
        ubit-agent (Rust, systemd) -> scores host inventory
        -> writes /var/lib/ubit/report/index.html (opened at login)
```

The host sends only app identifiers and default handlers. Scoring happens inside the
guest, against `catalog/`. Nothing leaves the machine. See `docs/threat-model.md`.

## Quick start (developers)

### Agent (any Linux, no VM needed)

```sh
cd agent
cargo test
cargo run -p ubit-agent -- --listen tcp:127.0.0.1:7710 \
    --catalog ../catalog --report-dir /tmp/ubit-report &
cargo run -p ubit-agent --bin ubit-hostsim -- --connect tcp:127.0.0.1:7710 \
    --inventory ../protocol/fixtures/valid/host_inventory-windows.json
open /tmp/ubit-report/index.html   # or xdg-open
```

### Image

Built in CI by `.github/workflows/image.yml` (manual run or `v*` tag). Locally, with
[mkosi](https://github.com/systemd/mkosi) v27+:

```sh
cd image
mkdir -p mkosi.packages && cp ../agent/target/debian/ubit-agent_*.deb mkosi.packages/
mkosi --profile=windows -f build    # x86_64, for Hyper-V
mkosi --profile=mac -f build        # arm64, for Apple Silicon (build on arm64)
```

### macOS host

```sh
cd hosts/macos
./scripts/fetch-image.sh saurintailor/ubitos   # downloads + verifies the arm64 image
./scripts/run-dev.sh                      # builds, ad-hoc signs, launches
```

### Windows host

See `hosts/windows/README.md`. In short: run `scripts\setup-home.ps1` (Windows Home) or
`scripts\setup-host.ps1` (Pro and up) once as Administrator, then
`dotnet run --project hosts\windows\src\Ubit.App`. The app picks QEMU or Hyper-V itself.

## Status

| Milestone | What | State |
| --- | --- | --- |
| M0 | Repo, CI, protocol schema v1 | done |
| M1 | Agent + scoring with tests | done: 18 tests; live interop with the Swift and C# clients |
| M2 | Image configs | written and parsed by mkosi 27.1; first real build runs in CI |
| M3 | macOS app | written; protocol and inventory tested (11 tests); VM code needs a Mac |
| M4 | Windows app | Hyper-V (Pro) and QEMU (Home) backends; core tested (34 tests); needs a run on Windows |
| M5 | Same score on both hosts | pending M3 and M4 runs |

## License

MIT, see `LICENSE`.
