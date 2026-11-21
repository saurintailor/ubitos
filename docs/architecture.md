# Architecture (v0)

The full technical design lives in the "Privacy Distro — Technical Design" doc. This page
is the short version that stays next to the code.

- **One image, two shells.** `image/` builds the same Ubuntu 26.04 + KDE Plasma guest for
  arm64 (Mac) and x86-64 (Windows). The host apps are thin: they own the VM and the
  display, and nothing else.
- **The agent is the product core.** `agent/` holds the protocol, the scoring rules and
  the daemon. Host apps send facts; the agent alone interprets them against `catalog/`.
- **One protocol, three implementations.** Rust (agent), Swift (Mac), C# (Windows), all
  tested against `protocol/fixtures/`. Change a message by adding a fixture first.
- **vsock where the hypervisor has it, virtio-serial where it doesn't.** The Mac app uses
  `VZVirtioSocketDevice`; the Windows app on Hyper-V uses `AF_HYPERV` sockets with the
  vsock template service id `00001e1e-facb-11e6-bd58-64006a7986d3` (port 7710). On
  Windows Home, QEMU has no vsock, so the agent also listens on the virtio-serial port
  `org.ubit.agent.0`, which QEMU exposes to the app as a Unix socket. Serial lines have no
  disconnect signal, so a repeated `hello` starts a session over.

## Verified so far

| Check | Where |
| --- | --- |
| Rust: 18 tests, clippy clean, `.deb` builds | Linux, CI `agent` |
| Swift protocol + inventory: 11 tests | Linux and CI `macos-host` |
| C# core: 34 tests | Linux and CI `windows-host` |
| C# client <-> Rust agent, Swift client <-> Rust agent, live over TCP | same score (70) for the same apps |
| Windows Home path rehearsed: Unix socket <-> serial port <-> agent that boots late | handshake retries, then scores |
| Fixtures <-> JSON Schema | CI `protocol` |
| mkosi config parses (both profiles) | mkosi 27.1 |

Not verified yet: a real image build, the Virtualization.framework and SwiftUI code (needs
a Mac), the Hyper-V WMI calls (need Windows Pro), and QEMU itself on Windows Home.
