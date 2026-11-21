# Threat model (draft, v0)

ubit asks people who care about privacy to install a host app and run a guest OS that
reads their app list. This page says exactly what each part can see and send.

## What crosses the host/guest boundary

| Data | Direction | Channel | Notes |
| --- | --- | --- | --- |
| App ids and names (bundle ids on macOS, Uninstall key names on Windows) | host -> guest | vsock 7710 | No file contents, paths, documents, history or credentials. The protocol has no field that could carry them. |
| Default browser and mail handler ids | host -> guest | vsock 7710 | e.g. `ChromeHTML`, `com.google.chrome` |
| Score, matched items, unknown app names | guest -> host | vsock 7710 | Shown in the host app |
| Same messages, Windows Home | both | virtio-serial port `org.ubit.agent.0` | QEMU serves it as a Unix socket in `%LOCALAPPDATA%\ubit\vm`, protected by the profile's permissions; never a TCP port |
| Clipboard text and images | both | SPICE port (Mac), RDP (Windows) | Provided by the hypervisor; the user can turn it off |
| Display, keyboard, mouse, sound out | both | hypervisor devices | No microphone or camera is attached on macOS |

## What leaves the machine

- **The host apps:** nothing. They make no network requests in v0. Images are fetched
  by the user (or `fetch-image.sh`) from GitHub releases.
- **The agent:** nothing. It listens on vsock and on one virtio-serial port. systemd blocks
  every IP address family for it (`RestrictAddressFamilies=AF_VSOCK AF_UNIX`,
  `IPAddressDeny=any`) and every device except that port (`DevicePolicy=closed`).
- **The guest OS:** Ubuntu and Mozilla package updates, NTP. No snap store, crash reporter,
  usage reporting, popularity contest, motd news, or Firefox telemetry and studies.

## Agent hardening

The agent runs as its own unprivileged user. It has no capabilities, a read-only system,
no home directories and private devices, and a `@system-service` syscall filter. It can
write only under `/var/lib/ubit`. Host input is size-limited (1 MiB frames, 5000 apps)
and every string is HTML-escaped before it reaches the report page. The page itself
carries a Content-Security-Policy that blocks scripts and remote loads.

## Known gaps (v0)

- Fixed trial password (`trial`) for xrdp sign-in.
- Secure Boot off; images are checksummed but not signed.
- Builds are pinned (package snapshot, `Cargo.lock`) but not yet byte-reproducible.
- Any process in the guest can connect to the host's vsock ports. The host apps only
  connect *to* the guest and never listen, so this exposes nothing today.
