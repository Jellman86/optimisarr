using System;
using System.Diagnostics;
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
        // Windows 11's Fluent controls and colours throughout, following the system's light,
        // dark, accent and contrast settings. WPF still labels ThemeMode an evaluation API (WPF0001)
        // although it ships in .NET 10; it is the only supported way to opt into Fluent, so the
        // diagnostic is suppressed here and where MonitorRenderer forces a scheme, nowhere else.
#pragma warning disable WPF0001
        var app = new TrayApp { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
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
        if (args.Contains("--export-diagnostics"))
        {
            if (IsAdministrator()) ExportLocalDiagnostics();
            else MessageBox.Show("Administrator access is required to read the worker's private diagnostic journal.", "Optimisarr Sidecar");
            Shutdown(); return;
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
        if (args.Contains("--set-settings"))
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            { MessageBox.Show("Run this action as administrator.", "Optimisarr Sidecar"); Shutdown(); return; }
            var idx = Array.IndexOf(args, "--set-settings");
            if (idx + 2 >= args.Length)
            { MessageBox.Show("Provide a work location and a concurrency.", "Optimisarr Sidecar"); Shutdown(); return; }
            SaveSettingsAndRestart(args[idx + 1], args[idx + 2]);
            Shutdown(); return;
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
        menu.Items.Add("Export local diagnostics… (administrator)", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (IsAdministrator()) { ExportLocalDiagnostics(); return; }
            try
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Optimisarr.Sidecar.Tray.exe"))
                { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--export-diagnostics");
                Process.Start(start);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Forms.MessageBox.Show("Diagnostic export was cancelled or administrator access could not start.", "Optimisarr Sidecar"); }
        }));
        menu.Items.Add("Settings… (work & concurrency)", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("Quit tray — keep worker running", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.ContextMenuStrip = menu;
        _ = WatchActivityAsync();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void ExportLocalDiagnostics()
    {
        using var save = new Forms.SaveFileDialog
        {
            Filter = "Diagnostic JSON|*.json",
            FileName = "optimisarr-sidecar-diagnostics.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            RestoreDirectory = true,
        };
        if (save.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var journal = new DiagnosticJournal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Optimisarr", "Sidecar"), readOnly: true);
            File.WriteAllBytes(save.FileName, journal.Export());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Forms.MessageBox.Show("Could not export diagnostics: " + error.Message, "Optimisarr Sidecar"); }
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

    private void ShowSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(24), Width = 400 };
        var window = new Window
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/BrandMark.png")),
            Title = "Work location & concurrency", Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
            Content = panel,
        };
        panel.Children.Add(new TextBlock { Text = "Work location & concurrency", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Where job source and candidate are written, and how many jobs run at once. This restarts the worker so the change takes effect (a running job hands back; originals are safe).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) });

        panel.Children.Add(new TextBlock { Text = "Work directory — a plain folder, or a RAM drive path (e.g. a mounted ImDisk) to work on RAM" });
        var work = new TextBox { Text = CurrentWorkLocation(), Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(work);

        panel.Children.Add(new TextBlock { Text = "Concurrent jobs (1–4) — a dGPU + iGPU box can fill both at once" });
        var conc = new ComboBox { Margin = new Thickness(0, 5, 0, 8) };
        for (var i = 1; i <= 4; i++) conc.Items.Add(i.ToString());
        conc.SelectedIndex = Math.Clamp(CurrentConcurrency() - 1, 0, 3);
        panel.Children.Add(conc);

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var apply = new Button { Content = "Save & restart worker", Padding = new Thickness(12), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (TryFindResource("AccentButtonStyle") is Style accent) apply.Style = accent;
        panel.Children.Add(apply);
        panel.Children.Add(message);

        apply.Click += (_, _) =>
        {
            var target = work.Text.Trim();
            if (string.IsNullOrWhiteSpace(target)) { message.Text = "Enter a work directory."; return; }
            var concurrency = (conc.SelectedItem as string) ?? "1";
            try
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Optimisarr.Sidecar.Tray.exe"))
                { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--set-settings");
                start.ArgumentList.Add(target);
                start.ArgumentList.Add(concurrency);
                Process.Start(start);
                window.Close();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { message.Text = "Administrator elevation was cancelled or could not start."; }
        };
        window.Show();
        work.Focus();
    }

    private static string CurrentWorkLocation()
    {
        var file = Path.Combine(ProgramData(), "work-location");
        try { if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } text) return text; } catch { }
        return Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_WORK") ?? @"C:\OptimisarrWork";
    }

    private static int CurrentConcurrency()
    {
        try
        {
            var file = Path.Combine(ProgramData(), "concurrency");
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var c) && c is >= 1 and <= 4) return c;
        }
        catch { }
        return int.TryParse(Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_CONCURRENCY"), out var e) && e is >= 1 and <= 4 ? e : 1;
    }

    private static string ProgramData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Optimisarr", "Sidecar");

    private static void SaveSettingsAndRestart(string work, string concurrency)
    {
        var dir = ProgramData();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "work-location"), work);
        File.WriteAllText(Path.Combine(dir, "concurrency"), concurrency);
        using var service = new System.ServiceProcess.ServiceController("OptimisarrSidecar");
        if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        service.Start();
        service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        Forms.MessageBox.Show($"Applied.\nWork directory: {work}\nConcurrent jobs: {concurrency}\n\nThe worker restarted with the new settings.", "Optimisarr Sidecar");
    }

    private void ShowSetup()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        var window = new Window { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/BrandMark.png")), Title = "Pair Optimisarr Sidecar", Width = 410, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Content = panel };
        panel.Children.Add(new TextBlock { Text = "Connect this PC", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Create a pairing code in Optimisarr → Settings → Workers.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "Server address" });
        var address = new TextBox { Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = "Pairing code" });
        var code = new PasswordBox { Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(code);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var pair = new Button { Content = "Pair & start worker", Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (TryFindResource("AccentButtonStyle") is Style accent) pair.Style = accent;
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
