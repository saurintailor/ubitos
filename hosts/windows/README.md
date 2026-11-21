# ubit for Windows

WPF app (.NET 10). It picks the hypervisor for you:

| Windows edition | Backend | Desktop | Agent channel |
| --- | --- | --- | --- |
| 11 Pro, Enterprise, Education | Hyper-V (WMI) | `vmconnect` enhanced session (xrdp) | hv_sock to vsock 7710 |
| 11 Home | QEMU + Windows Hypervisor Platform | QEMU's own window, signs in by itself | virtio-serial port, served as a Unix socket in your profile |

Set `UBIT_BACKEND=qemu` or `UBIT_BACKEND=hyperv` to force one.

## Windows 11 Home

1. Once, as Administrator: `scripts\setup-home.ps1`. It turns on Windows Hypervisor
   Platform and installs QEMU with winget. Restart if it asks.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then
   `dotnet run --project src\Ubit.App`.
3. **Choose image…** → pick the `.vhdx` (it is converted to qcow2, which takes a minute) →
   **Start**. A QEMU window opens and ubit signs in by itself; the score appears in the
   app after the guest boots.

## Windows 11 Pro, Enterprise, Education

1. Once, as Administrator: `scripts\setup-host.ps1`. It enables Hyper-V if needed, adds
   you to Hyper-V Administrators, and registers the agent's Hyper-V socket service.
   Then sign out and in.
2. `dotnet run --project src\Ubit.App`.
3. **Choose image…** → pick the `.vhdx` → **Start**. The desktop opens in Hyper-V's
   enhanced session. Sign in as `trial` / `trial` (preview only).

If VM creation fails, `scripts\dev-create-vm.ps1 -Vhdx <file>` creates the same VM with
the Hyper-V PowerShell module.

## Projects

| Project | What |
| --- | --- |
| `Ubit.Core` | Protocol (source-generated JSON), framing, `AgentClient`, `HvSocketEndPoint`, QEMU command line and QMP, inventory filter. Builds and tests anywhere. |
| `Ubit.Windows` | `IVmBackend` with `HyperVBackend` and `QemuBackend`, `HyperVManager` (WMI), `RegistryInventory`, `HostSetup` |
| `Ubit.App` | The window |

Not yet done: showing the desktop inside the app window, sound on the QEMU path, and an
installer.
