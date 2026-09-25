using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Service;
using Forms = System.Windows.Forms;

namespace Optimisarr.Sidecar.Tray;

public sealed class TrayApp : Application
{
    private Forms.NotifyIcon? tray;
    private TrayIconAnimator? trayIcon;
    private Forms.ToolStripMenuItem? shutdownItem;
    private readonly CancellationTokenSource activityLifetime = new();
    private Mutex? singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new TrayApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await app.StartAsync(args);
        app.Exit += (_, _) => { app.activityLifetime.Cancel(); app.trayIcon?.Dispose(); app.tray?.Dispose(); app.singleInstance?.Dispose(); };
        app.Run();
    }

    private async Task StartAsync(string[] args)
    {
        if (args.Contains("--verify-popover"))
        {
            var verification = new MonitorWindow(live: false);
            try { await verification.VerifyAnchoringAsync(); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Shutdown(1); }
            finally { verification.Close(); }
            return;
        }
        if (args.Contains("--render-monitor"))
        {
            var index = Array.IndexOf(args, "--render-monitor");
            if (index + 1 < args.Length) MonitorRenderer.Render(args[index + 1]);
            Shutdown();
            return;
        }
        if (args.Contains("--render-tray-motion"))
        {
            var index = Array.IndexOf(args, "--render-tray-motion");
            if (index + 1 < args.Length) TrayIconAnimator.RenderPreview(args[index + 1]);
            Shutdown();
            return;
        }
        if (args.Contains("--setup") || args.Contains("--start-worker"))
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            { MessageBox.Show("Run this action as administrator.", "Optimisarr Sidecar"); Shutdown(); return; }
            if (args.Contains("--setup")) ShowSetup();
            else
            {
                try { await StartWorkerAsync(); }
                catch (Exception e) { MessageBox.Show("Could not start the worker: " + e.Message, "Optimisarr Sidecar"); }
                Shutdown();
            }
            return;
        }
        singleInstance = new Mutex(true, "Local\\Optimisarr.Sidecar.Tray", out var created);
        if (!created) { Shutdown(); return; }
        var window = new MonitorWindow();
        MainWindow = window;
        using var resource = GetResourceStream(new Uri("pack://application:,,,/Resources/AppIcon.ico")).Stream;
        using var sourceIcon = new System.Drawing.Icon(resource);
        tray = new Forms.NotifyIcon { Text = "Optimisarr Sidecar — click for activity" };
        trayIcon = new TrayIconAnimator(tray, sourceIcon, SystemParameters.ClientAreaAnimation);
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(window.ShowAtTray); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open compact monitor", null, (_, _) => Dispatcher.Invoke(window.ShowAtTray));
        shutdownItem = new Forms.ToolStripMenuItem("Shut down when work is complete");
        shutdownItem.Click += async (_, _) =>
        {
            try
            {
                var armed = shutdownItem.Text == "Cancel shutdown";
                await MonitorClient.RequestAsync(armed ? MonitorProtocol.CancelShutdown : MonitorProtocol.ArmShutdown, activityLifetime.Token);
                await Dispatcher.InvokeAsync(() => { if (!window.IsVisible) window.ShowAtTray(); });
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Forms.MessageBox.Show("Could not contact the worker: " + error.Message, "Optimisarr Sidecar");
            }
        };
        menu.Items.Add(shutdownItem);
        menu.Items.Add("Quit tray — keep worker running", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.ContextMenuStrip = menu;
        _ = WatchActivityAsync();
    }

    private async Task WatchActivityAsync()
    {
        try
        {
            while (!activityLifetime.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await MonitorClient.RequestAsync(MonitorProtocol.Read, activityLifetime.Token);
                    trayIcon?.SetWorking(snapshot.Jobs.Count > 0);
                    if (shutdownItem is not null)
                    {
                        shutdownItem.Text = snapshot.ShutdownArmed ? "Cancel shutdown" : "Shut down when work is complete";
                        shutdownItem.Enabled = snapshot.ShutdownArmed
                            ? snapshot.ShutdownCanCancel : snapshot.State is "Connected" or "Working" or "Unreachable";
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    trayIcon?.SetWorking(false);
                    if (shutdownItem is not null) shutdownItem.Enabled = false;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), activityLifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ShowSetup()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        var window = new Window { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/BrandMark.png")), Title = "Pair Optimisarr Sidecar", Width = 410, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(16, 26, 44)), Foreground = Brushes.White, Content = panel };
        panel.Children.Add(new TextBlock { Text = "Connect this PC", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Create a pairing code in Optimisarr → Settings → Workers.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "Server address" });
        var address = new TextBox { Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = "Pairing code" });
        var code = new PasswordBox { Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(code);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var pair = new Button { Content = "Pair & start worker", Padding = new Thickness(12) };
        panel.Children.Add(pair);
        panel.Children.Add(message);
        var cancel = new CancellationTokenSource();
        window.Closed += (_, _) => { cancel.Cancel(); Shutdown(); };
        pair.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(address.Text) || string.IsNullOrWhiteSpace(code.Password))
            { message.Text = "Enter both the server address and pairing code."; return; }
            pair.IsEnabled = false;
            message.Text = "Checking capabilities and pairing…";
            try
            {
                using var service = new ServiceController("OptimisarrSidecar");
                if (service.Status != ServiceControllerStatus.Stopped)
                { message.Text = "The worker is already running. Stop it after its current job finishes before changing its pairing."; return; }
                await Program.PairForTrayAsync(address.Text.Trim(), code.Password.Trim(), cancel.Token);
                code.Clear();
                await StartWorkerAsync();
                message.Text = "Paired. The worker is running; open the tray to see its activity.";
            }
            catch (Exception e) { message.Text = "Could not complete setup: " + e.Message; }
            finally { pair.IsEnabled = true; }
        };
        window.Show();
        address.Focus();
    }

    private static Task StartWorkerAsync() => Task.Run(() =>
    {
        using var service = new ServiceController("OptimisarrSidecar");
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    });

}
