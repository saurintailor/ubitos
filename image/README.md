# ubit image

One [mkosi](https://github.com/systemd/mkosi) config builds both guest images:

| Profile | Arch | Host | Display | Clipboard | Artifact |
| --- | --- | --- | --- | --- | --- |
| `mac` | arm64 | Apple Virtualization.framework | virtio-gpu, SDDM autologin to Plasma (X11) | `spice-vdagent` via the SPICE console port | `ubit-mac-arm64-<v>.img.zst` |
| `windows` | x86-64 | Hyper-V Gen 2 | xrdp on hv_sock, via `vmconnect` enhanced session | RDP | `ubit-windows-x86_64-<v>.vhdx.zst` |

## Build

```sh
pip install "git+https://github.com/systemd/mkosi@v27.1"
scripts/fetch-mozilla-key.sh                 # re-verifies the committed Mozilla APT key
mkdir -p mkosi.packages && cp <ubit-agent_*.deb for the target arch> mkosi.packages/
mkdir -p mkosi.output
mkosi --profile=windows -f build             # or --profile=mac (build arm64 on an arm64 machine)
python3 scripts/check-manifest.py mkosi.output/ubit_0.1.0.manifest
scripts/package.sh windows                   # -> dist/
sudo scripts/smoke-boot.sh mkosi.output/ubit.raw ../agent/target/release/ubit-hostsim \
    ../protocol/fixtures/valid/host_inventory-windows.json
```

CI does all of this in `.github/workflows/image.yml` (manual run, or a `v*` tag, which
also attaches the artifacts to the GitHub release).

## What is (and isn't) in the image

- Ubuntu 26.04 LTS packages only, plus Firefox from `packages.mozilla.org` and our
  `ubit-agent` package.
- No snapd, ubuntu-report, popularity-contest, apport, whoopsie or ubuntu-pro-client. The
  build fails if any of them gets installed through a dependency.
- Firefox policies turn off telemetry, studies, Pocket and sponsored tiles, and open the
  readiness report on first run.
- `motd-news` is off, so nothing is fetched at login.

## v0 limitations

- **Fixed trial password.** User `trial`, password `trial`. It is there so xrdp has
  something to sign in with. A first-boot setup screen replaces it before any public build.
- **Secure Boot is off** on both hosts. Signed UKIs come with v1.
- **Plasma X11 session.** xrdp and spice-vdagent's clipboard need X11. Plasma is moving to
  Wayland only, so this needs a plan before Plasma drops X11.
- **Not built yet.** These configs parse cleanly with mkosi 27.1, but the first real build
  happens in CI. Expect to adjust a package name or two for 26.04.
