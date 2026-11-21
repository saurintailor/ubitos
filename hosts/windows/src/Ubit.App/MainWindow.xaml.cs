using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Ubit.Core.Protocol;
using Ubit.Windows;
using Ubit.Windows.Agent;
using Ubit.Windows.HyperV;
using Ubit.Windows.Inventory;

namespace Ubit.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "WPF windows are not IDisposable; agentCts is disposed in OnClosed.")]
public partial class MainWindow : Window
{
    /// <summary>Disk the guest gets; it grows its root partition to fill it on boot.</summary>
    private const ulong DiskSize = 64UL << 30;

    private static readonly string VmDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ubit", "vm");
    private static readonly string VhdxPath = Path.Combine(VmDirectory, "ubit.vhdx");
    private static readonly string ShellVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";

    private HyperVManager? hyperV;
    private CancellationTokenSource? agentCts;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        agentCts?.Cancel();
        agentCts?.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var problems = HostSetup.Problems();
        if (problems.Count > 0)
        {
            SetupText.Text = string.Join(Environment.NewLine, problems);
            SetupBanner.Visibility = Visibility.Visible;
        }
        await RunAsync(() => { hyperV = new HyperVManager(); }, "Connecting to Hyper-V…").ConfigureAwait(true);
        Refresh();
    }

    private async void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the ubit image",
            Filter = "Hyper-V disk (*.vhdx)|*.vhdx",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var source = dialog.FileName;
        await RunAsync(() =>
        {
            var vm = RequireHyperV().GetVm();
            if (vm.State == VmState.Running)
            {
                throw new InvalidOperationException("Shut ubit down before replacing its image.");
            }
            RequireHyperV().Delete();
            Directory.CreateDirectory(VmDirectory);
            File.Copy(source, VhdxPath, overwrite: true);
            RequireHyperV().ResizeDisk(VhdxPath, DiskSize);
            RequireHyperV().EnsureVm(VhdxPath);
        }, "Copying the image and creating the VM…").ConfigureAwait(true);
        Refresh();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await RunAsync(() =>
        {
            var vm = RequireHyperV().GetVm();
            if (vm.State == VmState.Missing)
            {
                throw new InvalidOperationException("Choose an image first.");
            }
            if (vm.State != VmState.Running)
            {
                RequireHyperV().Start();
            }
        }, "Starting ubit…").ConfigureAwait(true);
        Refresh();
        if (hyperV?.GetVm() is { State: VmState.Running } running)
        {
            HostSetup.OpenDesktop(running.Name);
            await TalkToAgentAsync(running.Id).ConfigureAwait(true);
        }
    }

    private void OnOpenDesktop(object sender, RoutedEventArgs e)
    {
        if (hyperV?.GetVm() is { State: VmState.Running } vm)
        {
            HostSetup.OpenDesktop(vm.Name);
        }
    }

    private async void OnShutdown(object sender, RoutedEventArgs e)
    {
        agentCts?.Cancel();
        await RunAsync(() => RequireHyperV().Shutdown(), "Shutting down…").ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Sends this PC's app list to ubit-agent and shows the score.</summary>
    private async Task TalkToAgentAsync(Guid vmId)
    {
        agentCts?.Cancel();
        agentCts?.Dispose();
        agentCts = new CancellationTokenSource();
        var ct = agentCts.Token;
        AgentText.Text = "Waiting for ubit to finish starting…";
        try
        {
            await using var client = await AgentClient.ConnectAsync(vmId, TimeSpan.FromMinutes(3), ct).ConfigureAwait(true);
            var ack = await client.HandshakeAsync(ShellVersion, ct).ConfigureAwait(true);
            AgentText.Text = "Scoring your apps…";
            var inventory = await Task.Run(RegistryInventory.Scan, ct).ConfigureAwait(true);
            var result = await client.SubmitAsync(inventory, ct).ConfigureAwait(true);
            ShowScore(result, inventory.Apps.Count, ack);
        }
        catch (OperationCanceledException)
        {
            AgentText.Text = "Stopped waiting for the agent.";
        }
        catch (Exception ex) when (ex is ProtocolException or System.Net.Sockets.SocketException or IOException)
        {
            AgentText.Text = $"Could not reach the agent: {ex.Message}";
        }
    }

    private void ShowScore(ScoreResult result, int appCount, HelloAck ack)
    {
        ScoreText.Text = result.Score?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "–";
        AgentText.Text = result.Score is null
            ? $"None of your {appCount} apps are in the catalog yet."
            : $"Based on {appCount} apps · {ack.DistroVersion} · agent {ack.AgentVersion}";
        Items.ItemsSource = result.Items.Select(i => new ItemRow(i)).ToList();
    }

    private void Refresh()
    {
        var vm = hyperV?.GetVm();
        StatusText.Text = vm?.State switch
        {
            null => "Hyper-V is not available.",
            VmState.Missing => File.Exists(VhdxPath) ? "Image copied; the VM is not created yet." : "No image yet. Choose the ubit .vhdx file.",
            VmState.Off => "Ready. Start ubit to try it.",
            VmState.Running => "Running. Open the desktop and sign in.",
            _ => "Busy (saved, paused or changing state).",
        };
        var exists = vm is not null && vm.State != VmState.Missing;
        StartButton.IsEnabled = exists && vm!.State != VmState.Running;
        DesktopButton.IsEnabled = vm?.State == VmState.Running;
        StopButton.IsEnabled = vm?.State == VmState.Running;
        ChooseButton.IsEnabled = hyperV is not null && vm?.State != VmState.Running;
    }

    private HyperVManager RequireHyperV() =>
        hyperV ?? throw new InvalidOperationException("Hyper-V is not available. See the setup note above.");

    /// <summary>Runs blocking Hyper-V work off the UI thread, with a busy bar and an error dialog.</summary>
    private async Task RunAsync(Action work, string busyText)
    {
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = busyText;
        IsEnabled = false;
        try
        {
            await Task.Run(work).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HyperVException or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or System.Management.ManagementException)
        {
            MessageBox.Show(this, ex.Message, "ubit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private sealed class ItemRow(ScoreItem item)
    {
        public string From { get; } = item.From;
        public string To { get; } = item.To;
        public string Status { get; } = item.Status.ToString();

        public Brush Badge { get; } = Brush(item.Status switch
        {
            ItemStatus.Native => "#E6F4EC",
            ItemStatus.Alternative => "#E8F0FB",
            ItemStatus.Workaround => "#FBF1E1",
            _ => "#FBE7E5",
        });

        public Brush BadgeInk { get; } = Brush(item.Status switch
        {
            ItemStatus.Native => "#2E7D4F",
            ItemStatus.Alternative => "#3B6FB6",
            ItemStatus.Workaround => "#8A5A12",
            _ => "#C0392B",
        });

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
