using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
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
    private readonly bool live;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private bool initialized;
    private System.Windows.Forms.Screen? anchorScreen;
    private bool positioning;

    public MonitorWindow(bool live = true)
    {
        this.live = live;
        InitializeComponent();
        DataContext = model;
        ApplyTheme();
        LoginToggle.IsChecked = live && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "OptimisarrSidecarTray", null) is not null;
        initialized = true;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!live) return;
            if (IsVisible) _ = RefreshAsync(CurrentReadCommand());
            else { model.ClearPreviews(); _ = StopPreviewsAsync(); }
        };
        Closed += (_, _) => lifetime.Cancel();
        SizeChanged += (_, _) => QueuePosition();
        DpiChanged += (_, _) => QueuePosition();
        if (live) _ = PollAsync();
    }

    public void ShowAtTray()
    {
        if (IsVisible) { Hide(); return; }
        anchorScreen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        ApplyTheme();
        Opacity = 0;
        Show();
        PositionAtTray();
        Opacity = 1;
        Activate();
    }

    private void QueuePosition()
    {
        if (IsVisible && anchorScreen is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionAtTray));
    }

    private void PositionAtTray()
    {
        if (positioning || !IsVisible || anchorScreen is null) return;
        positioning = true;
        try
        {
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == anchorScreen.DeviceName)
                ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            anchorScreen = screen;
            var hwnd = new WindowInteropHelper(this).Handle;
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
            MaxHeight = Math.Max(180, (screen.WorkingArea.Height - 8) / dpi);
            BodyScroll.MaxHeight = Math.Max(80, Math.Min(565, MaxHeight - 158));
            UpdateLayout();
            if (!GetWindowRect(hwnd, out var bounds)) return;
            var work = screen.WorkingArea;
            var full = screen.Bounds;
            var point = TrayPlacement.Place(new(full.Left, full.Top, full.Right, full.Bottom),
                new(work.Left, work.Top, work.Right, work.Bottom), bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(point.Left), (int)Math.Round(point.Top), 0, 0,
                0x0001 | 0x0004 | 0x0010); // Preserve size, z-order and activation while anchoring.
        }
        finally { positioning = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    internal async Task VerifyAnchoringAsync()
    {
        ShowAtTray();
        foreach (var page in new[] { ActivityPage, PreferencesPage, ActivityPage, DiagnosticsPage, ActivityPage })
        {
            Page(page);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PositionAtTray();
            var work = anchorScreen!.WorkingArea;
            GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds);
            if (bounds.Bottom > work.Bottom || bounds.Top < work.Top || Math.Abs(bounds.Bottom - (work.Bottom - 4)) > 2)
                throw new InvalidOperationException("Popup lost its taskbar anchor after navigation.");
        }
        foreach (var expanded in new[] { true, false, true, false })
        {
            ProcessingDetails.IsExpanded = expanded;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PositionAtTray();
            GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds);
            if (Math.Abs(bounds.Bottom - (anchorScreen!.WorkingArea.Bottom - 4)) > 2)
                throw new InvalidOperationException("Popup lost its taskbar anchor after disclosure.");
        }
        Hide();
    }

    internal void ApplyTheme(bool? forcedLight = null)
    {
        var light = forcedLight ?? (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value != 0);
        BrandImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(light ? "pack://application:,,,/Resources/BrandMarkLight.png" : "pack://application:,,,/Resources/BrandMark.png"));
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
                if (IsVisible) await RefreshAsync(CurrentReadCommand());
                await Task.Delay(2000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshAsync(byte command)
    {
        if (command is MonitorProtocol.Read or MonitorProtocol.ReadPreview)
        {
            if (!await requests.WaitAsync(0)) return;
        }
        else
        {
            try { await requests.WaitAsync(lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        try
        {
            var snapshot = await MonitorClient.RequestAsync(command, lifetime.Token);
            if (!IsVisible || ActivityPage.Visibility != Visibility.Visible)
                snapshot = snapshot with { Jobs = snapshot.Jobs.Select(job => job with { PreviewJpeg = null }).ToArray() };
            model.Update(snapshot);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            model.Disconnect("The worker is unavailable. Live readings have been cleared; try Start worker in Preferences.");
        }
        finally { requests.Release(); }
    }

    private byte CurrentReadCommand() => ActivityPage.Visibility == Visibility.Visible
        ? MonitorProtocol.ReadPreview : MonitorProtocol.Read;

    private async Task StopPreviewsAsync()
    {
        try { await MonitorClient.RequestAsync(MonitorProtocol.EndPreview, lifetime.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (model.Snapshot?.ShutdownArmed == true) return;
        await RefreshAsync(model.Snapshot?.Paused == true ? MonitorProtocol.Resume : MonitorProtocol.Pause);
        if (IsVisible && ActivityPage.Visibility == Visibility.Visible) await RefreshAsync(MonitorProtocol.ReadPreview);
    }
    private async void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(model.Snapshot?.ShutdownArmed == true ? MonitorProtocol.CancelShutdown : MonitorProtocol.ArmShutdown);
    }
    private void Back_Click(object sender, RoutedEventArgs e) => Page(ActivityPage);
    private void Preferences_Click(object sender, RoutedEventArgs e) => Page(PreferencesPage);
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Page(DiagnosticsPage);
    private void Page(StackPanel selected)
    {
        foreach (var page in new[] { ActivityPage, PreferencesPage, DiagnosticsPage }) page.Visibility = page == selected ? Visibility.Visible : Visibility.Collapsed;
        if (!live) return;
        if (selected == ActivityPage && IsVisible) _ = RefreshAsync(MonitorProtocol.ReadPreview);
        else { model.ClearPreviews(); _ = StopPreviewsAsync(); }
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
