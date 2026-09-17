using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Optimisarr.Sidecar.Service;
using Forms = System.Windows.Forms;

namespace Optimisarr.Sidecar.Tray;

public sealed class TrayApp : Application
{
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Mutex? singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new TrayApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => await app.StartAsync(args);
        app.Exit += (_, _) => { app.tray?.Dispose(); app.trayIcon?.Dispose(); app.singleInstance?.Dispose(); };
        app.Run();
    }

    private async Task StartAsync(string[] args)
    {
        if (args.Contains("--render-monitor"))
        {
            var index = Array.IndexOf(args, "--render-monitor");
            if (index + 1 < args.Length) MonitorRenderer.Render(args[index + 1]);
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
        using (var bitmap = new System.Drawing.Bitmap(32, 34))
        {
            using var drawing = System.Drawing.Graphics.FromImage(bitmap);
            drawing.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(123, 216, 209), 1.8f);
            drawing.DrawPolygon(pen, new System.Drawing.Point[] {new(16, 2), new(29, 9), new(29, 24), new(16, 32), new(3, 24), new(3, 9)});
            drawing.DrawLines(pen, new System.Drawing.Point[] {new(3, 9), new(16, 17), new(29, 9)});
            drawing.DrawLine(pen, 16, 17, 16, 32);
            var handle = bitmap.GetHicon();
            try { trayIcon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Optimisarr Sidecar — click for activity", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(window.ShowAtTray); };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open compact monitor", null, (_, _) => Dispatcher.Invoke(window.ShowAtTray));
        menu.Items.Add("Quit tray — keep worker running", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.ContextMenuStrip = menu;
    }

    private void ShowSetup()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        var window = new Window { Title = "Pair Optimisarr Sidecar", Width = 410, SizeToContent = SizeToContent.Height,
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
