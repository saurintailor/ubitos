using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Ubit.Core.Protocol;
using Ubit.Core.Qemu;
using Ubit.Windows.HyperV;

namespace Ubit.Windows;

/// <summary>One way of running the ubit VM on this PC.</summary>
public interface IVmBackend
{
    /// <summary>Shown in the window, e.g. "Hyper-V".</summary>
    string Name { get; }

    /// <summary>True when starting the VM opens its desktop window by itself.</summary>
    bool OpensOwnWindow { get; }

    /// <summary>Problems to fix before this backend can work; empty when ready.</summary>
    IReadOnlyList<string> SetupProblems();

    VmInfo GetVm();

    /// <summary>Copies the downloaded .vhdx into place, grows it, and creates the VM.</summary>
    void Import(string vhdxPath, ulong diskBytes);

    void Start();

    /// <summary>Asks the guest to shut down, like pressing its power button.</summary>
    void Shutdown();

    void OpenDesktop(VmInfo vm);

    /// <summary>Connects to ubit-agent and completes the hello exchange.</summary>
    Task<(AgentClient Client, HelloAck Ack)> ConnectAgentAsync(VmInfo vm, string shellVersion, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
public static class VmBackends
{
    public static readonly string VmDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ubit", "vm");

    /// <summary>
    /// Hyper-V when its management service is there (Windows Pro, Enterprise, Education),
    /// otherwise QEMU (Windows Home). UBIT_BACKEND=hyperv|qemu overrides the choice.
    /// </summary>
    public static IVmBackend Choose()
    {
        var forced = Environment.GetEnvironmentVariable("UBIT_BACKEND");
        if (string.Equals(forced, "qemu", StringComparison.OrdinalIgnoreCase))
        {
            return new QemuBackend();
        }
        if (string.Equals(forced, "hyperv", StringComparison.OrdinalIgnoreCase) || HyperVAvailable())
        {
            return new HyperVBackend();
        }
        return new QemuBackend();
    }

    private static bool HyperVAvailable()
    {
        try
        {
            new ManagementScope(@"\\.\root\virtualization\v2").Connect();
            return true;
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}

/// <summary>Hyper-V: WMI for the VM, vmconnect's enhanced session for the desktop, hv_sock for the agent.</summary>
[SupportedOSPlatform("windows")]
public sealed class HyperVBackend : IVmBackend
{
    private static readonly string Vhdx = Path.Combine(VmBackends.VmDirectory, "ubit.vhdx");
    private readonly Lazy<HyperVManager> hyperV = new(() => new HyperVManager());

    public string Name => "Hyper-V";
    public bool OpensOwnWindow => false;

    public IReadOnlyList<string> SetupProblems() => HostSetup.Problems();

    public VmInfo GetVm() => hyperV.Value.GetVm();

    public void Import(string vhdxPath, ulong diskBytes)
    {
        if (GetVm().State == VmState.Running)
        {
            throw new InvalidOperationException("Shut ubit down before replacing its image.");
        }
        Directory.CreateDirectory(VmBackends.VmDirectory);
        // Stage the new disk first: a failed copy or resize leaves the current VM intact.
        var staged = Path.Combine(VmBackends.VmDirectory, "ubit.importing.vhdx");
        File.Delete(staged);
        try
        {
            File.Copy(vhdxPath, staged);
            hyperV.Value.ResizeDisk(staged, diskBytes);
        }
        catch
        {
            File.Delete(staged);
            throw;
        }
        hyperV.Value.Delete();
        File.Move(staged, Vhdx, overwrite: true);
        hyperV.Value.EnsureVm(Vhdx);
    }

    public void Start() => hyperV.Value.Start();

    public void Shutdown() => hyperV.Value.Shutdown();

    public void OpenDesktop(VmInfo vm) => HostSetup.OpenDesktop(vm.Name);

    public async Task<(AgentClient Client, HelloAck Ack)> ConnectAgentAsync(VmInfo vm, string shellVersion, CancellationToken ct)
    {
        var client = await AgentClient.ConnectHyperVAsync(vm.Id, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        try
        {
            return (client, await client.HandshakeAsync(shellVersion, null, ct).ConfigureAwait(false));
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// QEMU with the Windows Hypervisor Platform: works on Windows Home. The display is
/// QEMU's own window; the agent talks over a virtio-serial port that QEMU serves as a
/// Unix socket inside the user's profile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QemuBackend : IVmBackend
{
    private static readonly string Disk = Path.Combine(VmBackends.VmDirectory, "ubit.qcow2");
    private static readonly string Vars = Path.Combine(VmBackends.VmDirectory, "efi-vars.fd");
    private static readonly string AgentSocket = Path.Combine(VmBackends.VmDirectory, "agent.sock");
    private static readonly string QmpSocket = Path.Combine(VmBackends.VmDirectory, "qmp.sock");
    private static readonly string PidFile = Path.Combine(VmBackends.VmDirectory, "qemu.pid");

    private readonly string? qemuDir = FindQemu();

    public string Name => "QEMU + Windows Hypervisor Platform";
    public bool OpensOwnWindow => true;

    private string QemuSystem => Path.Combine(qemuDir ?? "", "qemu-system-x86_64.exe");
    private string QemuImg => Path.Combine(qemuDir ?? "", "qemu-img.exe");
    private string FirmwareCode => Path.Combine(qemuDir ?? "", "share", "edk2-x86_64-code.fd");
    private string FirmwareVarsTemplate => Path.Combine(qemuDir ?? "", "share", "edk2-i386-vars.fd");

    public IReadOnlyList<string> SetupProblems()
    {
        if (qemuDir is null)
        {
            return [@"QEMU is not installed. Run scripts\setup-home.ps1 (it installs QEMU and turns on Windows Hypervisor Platform)."];
        }
        var problems = new List<string>();
        if (!File.Exists(FirmwareCode) || !File.Exists(FirmwareVarsTemplate))
        {
            problems.Add($"QEMU's UEFI firmware is missing from {Path.Combine(qemuDir, "share")}.");
        }
        return problems;
    }

    public VmInfo GetVm()
    {
        if (!File.Exists(Disk))
        {
            return new VmInfo(Guid.Empty, "ubit", VmState.Missing);
        }
        using var process = RunningProcess();
        return new VmInfo(Guid.Empty, "ubit", process is null ? VmState.Off : VmState.Running);
    }

    public void Import(string vhdxPath, ulong diskBytes)
    {
        RequireQemu();
        using (var running = RunningProcess())
        {
            if (running is not null)
            {
                throw new InvalidOperationException("Shut ubit down before replacing its image.");
            }
        }
        Directory.CreateDirectory(VmBackends.VmDirectory);
        // Stage the new disk; the current one is replaced only when conversion succeeds.
        var staged = Disk + ".importing";
        File.Delete(staged);
        try
        {
            RunTool(QemuImg, ["convert", "-O", "qcow2", vhdxPath, staged]);
            RunTool(QemuImg, ["resize", staged, diskBytes.ToString(CultureInfo.InvariantCulture)]);
        }
        catch
        {
            File.Delete(staged);
            throw;
        }
        File.Move(staged, Disk, overwrite: true);
        File.Copy(FirmwareVarsTemplate, Vars, overwrite: true);
    }

    public void Start()
    {
        RequireQemu();
        if (!File.Exists(Disk))
        {
            throw new InvalidOperationException("Choose an image first.");
        }
        using (var running = RunningProcess())
        {
            if (running is not null)
            {
                return;
            }
        }
        foreach (var stale in new[] { AgentSocket, QmpSocket, PidFile })
        {
            File.Delete(stale);
        }

        var options = new QemuOptions(FirmwareCode, Vars, Disk, AgentSocket, QmpSocket);
        var (pid, errors) = Launch(options);
        if (pid is null && errors.Contains("vdagent", StringComparison.OrdinalIgnoreCase))
        {
            // This QEMU build has no clipboard agent: run without clipboard sharing.
            (pid, errors) = Launch(options with { Clipboard = false });
        }
        if (pid is null)
        {
            throw new InvalidOperationException($"QEMU stopped right away: {errors.Trim()}");
        }
        File.WriteAllText(PidFile, pid.Value.ToString(CultureInfo.InvariantCulture));
    }

    public void Shutdown()
    {
        using var process = RunningProcess();
        if (process is null)
        {
            return;
        }
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(QmpSocket));
            using var stream = new NetworkStream(socket);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            QmpClient.ExecuteAsync(stream, "system_powerdown", timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            process.Kill();  // no control channel: close the VM window instead
        }
    }

    public void OpenDesktop(VmInfo vm)
    {
        // The desktop is QEMU's own window; it opened when the VM started.
    }

    public async Task<(AgentClient Client, HelloAck Ack)> ConnectAgentAsync(VmInfo vm, string shellVersion, CancellationToken ct)
    {
        var client = await AgentClient.ConnectUnixAsync(AgentSocket, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        try
        {
            // The socket accepts as soon as QEMU runs; the guest needs a minute or two to boot.
            var ack = await client.HandshakeWithRetryAsync(shellVersion, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(4), ct)
                .ConfigureAwait(false);
            return (client, ack);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts QEMU and watches it for a few seconds. Returns its process id, or null and
    /// its error output if it exited (unsupported option, accelerator problem).
    /// </summary>
    private (int? Pid, string Errors) Launch(QemuOptions options)
    {
        var start = new ProcessStartInfo(QemuSystem)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in QemuCommand.Arguments(options))
        {
            start.ArgumentList.Add(arg);
        }
        var errors = new StringBuilder();
        var process = Process.Start(start) ?? throw new InvalidOperationException("QEMU did not start.");
        process.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                if (e.Data is not null && errors.Length < 4000)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();
        if (process.WaitForExit(TimeSpan.FromSeconds(4)))
        {
            process.WaitForExit();  // let the error reader finish
            process.Dispose();
            lock (errors)
            {
                return (null, errors.ToString());
            }
        }
        var pid = process.Id;
        process.Dispose();  // QEMU keeps running; it is tracked by pid
        return (pid, "");
    }

    private void RequireQemu()
    {
        var problems = SetupProblems();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }
    }

    private static Process? RunningProcess()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                return null;
            }
            var process = Process.GetProcessById(pid);
            if (process.HasExited || !process.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException)
        {
            return null;  // no such process
        }
    }

    private static void RunTool(string exe, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{exe} did not start.");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"{Path.GetFileName(exe)} failed: {errors.Trim()}");
        }
    }

    /// <summary>UBIT_QEMU_DIR, then the default install folder, then PATH.</summary>
    private static string? FindQemu()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("UBIT_QEMU_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return candidates.FirstOrDefault(dir =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "qemu-system-x86_64.exe")));
    }
}
