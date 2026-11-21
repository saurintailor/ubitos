using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Ubit.Core.Protocol;
using Ubit.Windows;
using Ubit.Windows.HyperV;
using Ubit.Windows.Inventory;

namespace Ubit.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "WPF windows are not IDisposable; agentCts is disposed in OnClosed.")]
public partial class MainWindow : Window
{
    /// <summary>Disk the guest gets; it grows its root partition to fill it on boot.</summary>
    private const ulong DiskSize = 64UL << 30;

    private static readonly string ShellVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";

    private IVmBackend? backend;
    private CancellationTokenSource? agentCts;
    private readonly System.Windows.Threading.DispatcherTimer stateTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        // Catch state changes made outside the app, e.g. shutting down from inside ubit.
        stateTimer.Tick += async (_, _) =>
        {
            if (!busy && backend is not null)
            {
                var state = await Task.Run(() => backend.GetVm()).ConfigureAwait(true);
                Refresh(state);
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        stateTimer.Stop();
        agentCts?.Cancel();
        agentCts?.Dispose();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => { backend = VmBackends.Choose(); }, "Looking for a hypervisor…").ConfigureAwait(true);
        if (backend is null)
        {
            return;
        }
        Title = $"ubit · {backend.Name}";
        var problems = await Task.Run(backend.SetupProblems).ConfigureAwait(true);
        if (problems.Count > 0)
        {
            SetupText.Text = string.Join(Environment.NewLine, problems);
            SetupHint.Text = backend is QemuBackend
                ? @"Run scripts\setup-home.ps1 as Administrator, then restart Windows if it asks."
                : @"Run scripts\setup-host.ps1 as Administrator, then sign out and back in.";
            SetupBanner.Visibility = Visibility.Visible;
        }
        Refresh();
        stateTimer.Start();
        // The VM may still be running from an earlier session of this app: reconnect.
        if (backend.GetVm() is { State: VmState.Running } running)
        {
            await TalkToAgentAsync(running).ConfigureAwait(true);
        }
    }

    private async void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the ubit image",
            Filter = "ubit Windows image (*.vhdx)|*.vhdx",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var source = dialog.FileName;
        await RunAsync(() => RequireBackend().Import(source, DiskSize), "Copying the image and creating the VM…")
            .ConfigureAwait(true);
        Refresh();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await RunAsync(() =>
        {
            var b = RequireBackend();
            if (b.GetVm().State == VmState.Missing)
            {
                throw new InvalidOperationException("Choose an image first.");
            }
            b.Start();
        }, "Starting ubit…").ConfigureAwait(true);
        Refresh();
        if (backend?.GetVm() is { State: VmState.Running } running)
        {
            if (!backend.OpensOwnWindow)
            {
                backend.OpenDesktop(running);
            }
            await TalkToAgentAsync(running).ConfigureAwait(true);
        }
    }

    private void OnOpenDesktop(object sender, RoutedEventArgs e)
    {
        if (backend?.GetVm() is { State: VmState.Running } vm)
        {
            backend.OpenDesktop(vm);
        }
    }

    private async void OnShutdown(object sender, RoutedEventArgs e)
    {
        agentCts?.Cancel();
        await RunAsync(() => RequireBackend().Shutdown(), "Shutting down…").ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Sends this PC's app list to ubit-agent and shows the score.</summary>
    private async Task TalkToAgentAsync(VmInfo vm)
    {
        agentCts?.Cancel();
        agentCts?.Dispose();
        agentCts = new CancellationTokenSource();
        var ct = agentCts.Token;
        AgentText.Text = "Waiting for ubit to finish starting…";
        try
        {
            var (client, ack) = await RequireBackend().ConnectAgentAsync(vm, ShellVersion, ct).ConfigureAwait(true);
            await using (client.ConfigureAwait(true))
            {
                AgentText.Text = "Scoring your apps…";
                var inventory = await Task.Run(RegistryInventory.Scan, ct).ConfigureAwait(true);
                var result = await client.SubmitAsync(inventory, ct).ConfigureAwait(true);
                ShowScore(result, inventory.Apps.Count, ack);
            }
        }
        catch (OperationCanceledException)
        {
            AgentText.Text = ct.IsCancellationRequested ? "Stopped waiting for the agent." : "ubit did not answer in time.";
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

    private void Refresh() => Refresh(backend?.GetVm());

    private void Refresh(VmInfo? vm)
    {
        var ownWindow = backend?.OpensOwnWindow == true;
        StatusText.Text = vm?.State switch
        {
            null => "No hypervisor is available. See the setup note above.",
            VmState.Missing => "No image yet. Choose the ubit .vhdx file.",
            VmState.Off => "Ready. Start ubit to try it.",
            VmState.Running => ownWindow
                ? "Running in its own window. It signs in by itself."
                : "Running. Open the desktop and sign in.",
            _ => "Busy (saved, paused or changing state).",
        };
        var exists = vm is not null && vm.State != VmState.Missing;
        StartButton.IsEnabled = exists && vm!.State != VmState.Running;
        DesktopButton.IsEnabled = vm?.State == VmState.Running && !ownWindow;
        StopButton.IsEnabled = vm?.State == VmState.Running;
        ChooseButton.IsEnabled = backend is not null && vm?.State != VmState.Running;
    }

    private IVmBackend RequireBackend() =>
        backend ?? throw new InvalidOperationException("No hypervisor is available. See the setup note above.");

    /// <summary>Runs blocking VM work off the UI thread, with a busy bar and an error dialog.</summary>
    private async Task RunAsync(Action work, string busyText)
    {
        busy = true;
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = busyText;
        IsEnabled = false;
        try
        {
            await Task.Run(work).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HyperVException or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or System.Management.ManagementException
                                   or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "ubit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
            busy = false;
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
