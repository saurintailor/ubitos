# ubit for Windows

WPF app (.NET 10) on Hyper-V. Windows 11 Pro, Enterprise or Education.

1. Once, as Administrator: `scripts\setup-host.ps1`. It enables Hyper-V if needed, adds
   you to Hyper-V Administrators, and registers the agent's Hyper-V socket service.
   Then sign out and in.
2. `dotnet run --project src\Ubit.App` (or open `Ubit.sln`).
3. **Choose image…** → pick the `.vhdx` → **Start**. The desktop opens in Hyper-V's
   enhanced session. Sign in as `trial` / `trial` (preview only).

| Project | What |
| --- | --- |
| `Ubit.Core` | Protocol (source-generated JSON), framing, `HvSocketEndPoint`, inventory filter. Builds and tests anywhere. |
| `Ubit.Windows` | `HyperVManager` (WMI), `AgentClient` (hv_sock), `RegistryInventory`, `HostSetup` |
| `Ubit.App` | The window |

If VM creation from the app fails, `scripts\dev-create-vm.ps1 -Vhdx <file>` creates the
same VM with the Hyper-V PowerShell module, and the app can then start it.

Not yet done: Windows Home (no Hyper-V manager), showing the desktop inside the app
window instead of `vmconnect`, and an installer.
