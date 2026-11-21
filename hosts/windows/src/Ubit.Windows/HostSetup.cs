using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Ubit.Core.Protocol;
using Ubit.Core.Transport;

namespace Ubit.Windows;

/// <summary>Checks the one-time host setup that scripts\setup-host.ps1 performs.</summary>
[SupportedOSPlatform("windows")]
public static class HostSetup
{
    private const string ServicesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices";
    private static readonly SecurityIdentifier HyperVAdministrators = new("S-1-5-32-578");

    public static Guid AgentServiceId => HvSocketEndPoint.ServiceIdForVsockPort(ProtocolInfo.AgentVsockPort);

    /// <summary>Problems to fix before the app can work; empty when setup is complete.</summary>
    public static IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{AgentServiceId:D}"))
        {
            if (key is null)
            {
                problems.Add("The ubit agent service is not registered with Hyper-V.");
            }
        }
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(HyperVAdministrators) && !principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                problems.Add("Your account is not in the Hyper-V Administrators group (sign out and in after adding it).");
            }
        }
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "vmconnect.exe")))
        {
            problems.Add("Hyper-V is not enabled on this PC.");
        }
        return problems;
    }

    /// <summary>Opens the VM's desktop in Hyper-V's enhanced session (xrdp in the guest).</summary>
    public static void OpenDesktop(string vmName)
    {
        var vmconnect = Path.Combine(Environment.SystemDirectory, "vmconnect.exe");
        using var process = Process.Start(new ProcessStartInfo(vmconnect, ["localhost", vmName]) { UseShellExecute = false });
    }
}
