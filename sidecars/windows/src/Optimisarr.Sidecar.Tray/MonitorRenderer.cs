using System.IO;
using System;
using System.Linq;
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
        foreach (var state in new[] { "idle", "encoding", "verifying", "offline", "light-encoding", "details", "light-details", "preferences", "light-preferences", "preview-fallback", "light-preview-fallback", "two-jobs", "light-two-jobs", "many-jobs", "light-many-jobs", "shutdown-countdown", "light-shutdown-countdown" })
        {
            var pose = state.Replace("light-", "");
            var model = new MonitorViewModel();
            var preview = pose == "preview-fallback" ? null : DemoFrame(Color.FromRgb(24, 161, 182));
            var jobs = pose is "encoding" or "verifying" or "details" or "preview-fallback" or "two-jobs" or "many-jobs"
                ? new[] { new MonitorJob(42, "Prism Field · Demo clip", "hevc_nvenc",
                    pose != "verifying" ? RemoteStage.Encoding : RemoteStage.Measuring, 92, preview) }
                : [];
            if (pose == "two-jobs") jobs = [.. jobs, new MonitorJob(43, "Orbit Study · Demo clip", "hevc_nvenc",
                RemoteStage.Encoding, 48, DemoFrame(Color.FromRgb(180, 120, 220)))];
            if (pose == "many-jobs") jobs = [.. jobs, .. Enumerable.Range(43, 4).Select(id => new MonitorJob(id,
                $"Synthetic job {id} · Demo clip", "hevc_nvenc", RemoteStage.Encoding, id,
                id < 46 ? DemoFrame(Color.FromRgb(180, 120, 220)) : null))];
            model.Update(new MonitorSnapshot("Studio PC", "Connected", "Connected to server", false, "https://optimisarr.example.com",
                new MachineLoad(0.18, 0.64), 428L * 1_073_741_824,
                jobs,
                "Job #41: candidate returned to server", SidecarBuild.Version,
                pose == "shutdown-countdown", pose == "shutdown-countdown" ? "No jobs held. Shutting down in 48 seconds; cancel at any time." : null,
                pose == "shutdown-countdown" ? 48 : null));
            if (pose == "offline") model.Disconnect("The worker is unavailable. Live readings have been cleared.");
            var window = new MonitorWindow(live: false) { DataContext = model };
            window.ProcessingDetails.IsExpanded = pose is "details" or "two-jobs" or "many-jobs";
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

    private static byte[] DemoFrame(Color accent)
    {
        var drawing = new DrawingVisual();
        using (var painter = drawing.RenderOpen())
        {
            painter.DrawRectangle(new LinearGradientBrush(Color.FromRgb(11, 24, 46), accent, 25),
                null, new Rect(0, 0, 160, 90));
            painter.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 232, 250, 255)), null,
                new Point(105, 34), 19, 19);
            painter.DrawRectangle(new SolidColorBrush(Color.FromArgb(155, 9, 23, 42)), null,
                new Rect(0, 67, 160, 23));
            painter.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1),
                new Point(0, 66), new Point(160, 66));
        }
        var bitmap = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();
        if (bytes.Length > MonitorProtocol.MaximumPreviewBytes) throw new InvalidOperationException("Demo preview exceeded the monitor bound.");
        return bytes;
    }
}
