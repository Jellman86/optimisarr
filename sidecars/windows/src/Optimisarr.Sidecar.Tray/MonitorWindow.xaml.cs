using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

public partial class MonitorWindow : Window
{
    private readonly MonitorViewModel model = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private bool initialized;

    public MonitorWindow()
    {
        InitializeComponent();
        DataContext = model;
        ApplyTheme();
        LoginToggle.IsChecked = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "OptimisarrSidecarTray", null) is not null;
        initialized = true;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshAsync(MonitorProtocol.Read); };
        Closed += (_, _) => lifetime.Cancel();
        _ = PollAsync();
    }

    public void ShowAtTray()
    {
        ApplyTheme();
        Show();
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = scale.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = scale.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        MaxHeight = Math.Max(240, bottomRight.Y - topLeft.Y - 20);
        BodyScroll.MaxHeight = Math.Min(565, MaxHeight - 150);
        UpdateLayout();
        Left = Math.Max(topLeft.X + 10, bottomRight.X - ActualWidth - 12);
        Top = Math.Max(topLeft.Y + 10, bottomRight.Y - ActualHeight - 12);
        Activate();
    }

    internal void ApplyTheme(bool? forcedLight = null)
    {
        var light = forcedLight ?? (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value != 0);
        string[] names = ["Ground", "Surface", "Raised", "Line", "Ink", "Muted", "Accent"];
        string[] colours = light ? ["#F3F6FA", "#FFFFFF", "#E5EDF5", "#CAD5E2", "#152338", "#50637D", "#087E8B"] : ["#101A2C", "#18253B", "#203149", "#34455F", "#EFF4FC", "#B0BDD1", "#7BD8D1"];
        for (var index = 0; index < names.Length; index++) Resources[names[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colours[index]));
        Resources["CardFill"] = light
            ? new LinearGradientBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(235, 241, 248), 45)
            : new LinearGradientBrush(Color.FromRgb(32, 49, 73), Color.FromRgb(21, 32, 53), 45);
        if (SystemParameters.HighContrast)
        {
            Resources["CardFill"] = Resources["Ground"] = Resources["Surface"] = Resources["Raised"] = SystemColors.WindowBrush;
            Resources["Ink"] = Resources["Muted"] = SystemColors.WindowTextBrush;
            Resources["Line"] = Resources["Accent"] = SystemColors.HighlightBrush;
        }
        // A reduced-motion desktop still gets an honest activity label, without an animated sweep.
        WorkProgress.IsIndeterminate = SystemParameters.ClientAreaAnimation;
    }

    private async Task PollAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (IsVisible) await RefreshAsync(MonitorProtocol.Read);
                await Task.Delay(2000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshAsync(byte command)
    {
        if (!await requests.WaitAsync(0)) return;
        try { model.Update(await MonitorClient.RequestAsync(command, lifetime.Token)); }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            model.Disconnect("The worker is unavailable. Live readings have been cleared; try Start worker in Preferences.");
        }
        finally { requests.Release(); }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await RefreshAsync(model.Snapshot?.Paused == true ? MonitorProtocol.Resume : MonitorProtocol.Pause);
    private void Back_Click(object sender, RoutedEventArgs e) => Page(ActivityPage);
    private void Preferences_Click(object sender, RoutedEventArgs e) => Page(PreferencesPage);
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Page(DiagnosticsPage);
    private void Page(StackPanel selected)
    {
        foreach (var page in new[] { ActivityPage, PreferencesPage, DiagnosticsPage }) page.Visibility = page == selected ? Visibility.Visible : Visibility.Collapsed;
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
    private void OpenServer_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorProtocol.ServerUri(model.Snapshot?.ServerAddress) is { } uri) Open(uri.AbsoluteUri);
    }
    private void Events_Click(object sender, RoutedEventArgs e) => Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "eventvwr.msc"));
    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    private void Pair_Click(object sender, RoutedEventArgs e) => Elevate("--setup");
    private void Start_Click(object sender, RoutedEventArgs e) => Elevate("--start-worker");
    private static string TrayExecutable => Path.Combine(AppContext.BaseDirectory, "Optimisarr.Sidecar.Tray.exe");

    private void Elevate(string command)
    {
        try
        {
            var start = new ProcessStartInfo(TrayExecutable) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(command);
            Process.Start(start);
            ActionMessage.Text = "Complete the administrator action, then return here.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ActionMessage.Text = "The administrator action was cancelled or could not start."; }
    }
    private void Login_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (LoginToggle.IsChecked == true) key.SetValue("OptimisarrSidecarTray", "\"" + TrayExecutable + "\"");
            else key.DeleteValue("OptimisarrSidecarTray", false);
            ActionMessage.Text = "Sign-in preference saved.";
        }
        catch (Exception problem) when (problem is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { ActionMessage.Text = "Windows could not save the sign-in preference."; }
    }
    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { model.Disconnect("Windows could not open the requested application."); }
    }
}
