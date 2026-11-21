# ubit for macOS

SwiftUI app on Virtualization.framework. Apple Silicon, macOS 14+.

```sh
./scripts/fetch-image.sh saurintailor/ubitos   # downloads + verifies ubit-mac-arm64.img (needs gh, zstd)
./scripts/run-dev.sh                  # builds, signs ad hoc with the virtualization entitlement, runs
swift test                            # protocol + inventory tests (also run on Linux)
```

In the app: **Choose image…** → pick `ubit-mac-arm64.img` → **Start**. The guest logs in
automatically, opens the readiness report, and the toolbar shows the score.

| Module | What |
| --- | --- |
| `UbitProtocol` | Messages, framing, `AgentSession`. Foundation only. |
| `UbitCore` | `VMBundle` (disk, EFI vars, MAC), `VMConfigurationBuilder`, `AgentLink` (vsock), `InventoryScanner` |
| `UbitMac` | The app: `AppModel` (lifecycle), `ContentView`, `VMView` |

Not yet done: downloading the image from inside the app, a signed and notarized `.app`
bundle, and shared folders (virtio-fs).
