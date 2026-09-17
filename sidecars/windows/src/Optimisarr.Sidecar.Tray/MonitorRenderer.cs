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
        foreach (var state in new[] { "idle", "encoding", "verifying", "offline", "light-encoding" })
        {
            var pose = state.Replace("light-", "");
            var model = new MonitorViewModel();
            model.Update(new MonitorSnapshot("PICARD", "Connected", "Connected to server", false, "https://optimisarr.example.com",
                new MachineLoad(0.18, 0.64), 428L * 1_073_741_824,
                pose is "encoding" or "verifying" ? [new MonitorJob(42, "Big Buck Bunny", "hevc_nvenc", pose == "encoding" ? RemoteStage.Encoding : RemoteStage.Measuring, 92)] : [],
                "Job #41: candidate returned to server", "0.2.12"));
            if (pose == "offline") model.Disconnect("The worker is unavailable. Live readings have been cleared.");
            var window = new MonitorWindow { DataContext = model };
            window.ApplyTheme(state.StartsWith("light-", System.StringComparison.Ordinal));
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(410, 720));
            content.Arrange(new Rect(0, 0, 410, System.Math.Ceiling(content.DesiredSize.Height) + 12));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(820, (int)(content.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, state + ".png"));
            png.Save(file);
            window.Close();
        }
    }
}
