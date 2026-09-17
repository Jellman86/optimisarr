using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

internal static class MonitorRenderer
{
    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var state in new[] { "idle", "encoding", "verifying", "offline", "light-encoding", "details", "light-details", "preferences", "light-preferences" })
        {
            var pose = state.Replace("light-", "");
            var model = new MonitorViewModel();
            model.Update(new MonitorSnapshot("Studio PC", "Connected", "Connected to server", false, "https://optimisarr.example.com",
                new MachineLoad(0.18, 0.64), 428L * 1_073_741_824,
                pose is "encoding" or "verifying" or "details" ? [new MonitorJob(42, "Prism Field · Demo clip", "hevc_nvenc", pose != "verifying" ? RemoteStage.Encoding : RemoteStage.Measuring, 92)] : [],
                "Job #41: candidate returned to server", SidecarBuild.Version));
            if (pose == "offline") model.Disconnect("The worker is unavailable. Live readings have been cleared.");
            var window = new MonitorWindow(live: false) { DataContext = model };
            window.ProcessingDetails.IsExpanded = pose == "details";
            if (pose == "preferences")
            {
                window.ActivityPage.Visibility = Visibility.Collapsed;
                window.PreferencesPage.Visibility = Visibility.Visible;
            }
            window.ApplyTheme(state.StartsWith("light-", System.StringComparison.Ordinal));
            var content = (FrameworkElement)window.Content;
            // Detach the fixture visual so an unseen Window cannot remeasure/clip it during
            // UpdateLayout. Preserve the native window's inherited theme and typography.
            window.Content = null;
            content.DataContext = model;
            content.Resources = window.Resources;
            content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
            content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
            content.Measure(new Size(410, double.PositiveInfinity));
            var height = System.Math.Ceiling(content.DesiredSize.Height) + 12;
            content.Arrange(new Rect(0, 0, 410, height));
            content.UpdateLayout();
            // Disclosure content can grow during arrange; capture its final bounds and shadow.
            height = System.Math.Ceiling(System.Math.Max(height, VisualTreeHelper.GetDescendantBounds(content).Bottom + 16));
            var bitmap = new RenderTargetBitmap(820, (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, state + ".png"));
            png.Save(file);
            window.Close();
        }
    }
}
