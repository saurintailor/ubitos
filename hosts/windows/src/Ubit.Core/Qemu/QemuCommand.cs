namespace Ubit.Core.Qemu;

/// <summary>Everything needed to start the ubit guest under QEMU.</summary>
/// <param name="FirmwareCode">edk2-x86_64-code.fd from the QEMU install.</param>
/// <param name="FirmwareVars">This VM's own copy of the UEFI variable store.</param>
/// <param name="Disk">The qcow2 disk.</param>
/// <param name="AgentSocket">Unix socket QEMU serves for the agent's virtio-serial port.</param>
/// <param name="QmpSocket">Unix socket for QEMU's control protocol (used to shut down cleanly).</param>
/// <param name="Clipboard">Share the clipboard through QEMU's built-in SPICE agent (not in every QEMU build).</param>
public sealed record QemuOptions(
    string FirmwareCode,
    string FirmwareVars,
    string Disk,
    string AgentSocket,
    string QmpSocket,
    int MemoryMb = 4096,
    int Processors = 2,
    bool Clipboard = true);

/// <summary>
/// Builds the qemu-system-x86_64 command line for the Windows Home path: the Windows
/// Hypervisor Platform accelerator (falling back to software emulation), virtio
/// devices, a GTK window as the display, clipboard through QEMU's built-in SPICE
/// agent, and the agent channel on a virtio-serial port served as a Unix socket in
/// the user's profile (never a network port).
/// </summary>
public static class QemuCommand
{
    /// <summary>Name the guest agent looks for under /dev/virtio-ports/.</summary>
    public const string AgentPortName = "org.ubit.agent.0";

    /// <summary>Tells the guest to sign in on its own display (see ubit-console-session.service).</summary>
    public const string ConsoleCmdline = "ubit.console=1";

    public static IReadOnlyList<string> Arguments(QemuOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        List<string> args =
        [
            "-name", "ubit",
            "-machine", "q35",
            // Accelerators are tried in order: WHPX first, software emulation if it is off.
            "-accel", "whpx,kernel-irqchip=off",
            "-accel", "tcg",
            "-m", o.MemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-smp", o.Processors.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-drive", $"if=pflash,format=raw,readonly=on,file={Escape(o.FirmwareCode)}",
            "-drive", $"if=pflash,format=raw,file={Escape(o.FirmwareVars)}",
            "-drive", $"if=virtio,format=qcow2,file={Escape(o.Disk)}",
            "-nic", "user,model=virtio-net-pci",
            "-device", "virtio-vga",
            "-display", "gtk,zoom-to-fit=on",
            "-device", "qemu-xhci",
            "-device", "usb-tablet",
            "-device", "usb-kbd",
            "-device", "virtio-serial-pci",
            "-chardev", $"socket,id=ubitagent,path={Escape(o.AgentSocket)},server=on,wait=off",
            "-device", $"virtserialport,chardev=ubitagent,name={AgentPortName}",
            "-qmp", $"unix:{Escape(o.QmpSocket)},server=on,wait=off",
            "-smbios", $"type=11,value=io.systemd.stub.kernel-cmdline-extra={ConsoleCmdline}",
        ];
        if (o.Clipboard)
        {
            args.AddRange([
                "-chardev", "qemu-vdagent,id=vdagent,clipboard=on",
                "-device", "virtserialport,chardev=vdagent,name=com.redhat.spice.0",
            ]);
        }
        return args;
    }

    /// <summary>QEMU option values use commas as separators; a literal comma is doubled.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace(",", ",,", StringComparison.Ordinal);
    }
}
