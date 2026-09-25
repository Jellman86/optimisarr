using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace Optimisarr.Sidecar.Tray;

internal sealed class TrayIconAnimator : IDisposable
{
    private readonly Forms.NotifyIcon tray;
    private readonly Icon[] darkFrames;
    private readonly Icon[] lightFrames;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch elapsed = new();
    private bool animationEnabled;
    private readonly Size size;
    private Icon[]? settling;
    private bool working;
    private bool disposed;
    private bool lightSystemTheme;

    internal TrayIconAnimator(Forms.NotifyIcon tray, Icon source, bool animationEnabled)
    {
        this.tray = tray;
        this.animationEnabled = animationEnabled;
        lightSystemTheme = IsLightSystemTheme();
        size = Forms.SystemInformation.SmallIconSize;
        using var dark = LoadBitmap("BrandMotion.png");
        using var light = LoadBitmap("BrandMotionLight.png");
        using var lightIdle = LoadBitmap("BrandMarkLight.png");
        using var lightMark = DrawIcon(lightIdle, size);
        darkFrames = BuildFrames(dark, new Icon(source, size), size);
        lightFrames = BuildFrames(light, IconFromBitmap(lightMark), size);
        tray.Icon = RestFrame();
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000d / ActivityIconFrame.FramesPerSecond)
        };
        timer.Tick += (_, _) => Paint();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        timer.Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            var nextAnimation = SystemParameters.ClientAreaAnimation;
            var nextLight = IsLightSystemTheme();
            if (animationEnabled == nextAnimation && lightSystemTheme == nextLight) return;
            animationEnabled = nextAnimation;
            lightSystemTheme = nextLight;
            timer.Stop();
            DisposeSettling();
            elapsed.Restart();
            if (working && animationEnabled) timer.Start();
            Paint();
        });
    }

    internal void SetWorking(bool next)
    {
        if (working == next) return;
        var prior = working && animationEnabled ? tray.Icon : null;
        working = next;
        timer.Stop();
        DisposeSettling();
        elapsed.Restart();
        if (working && animationEnabled)
        {
            timer.Start();
            Paint();
        }
        else if (prior is not null)
        {
            // Prepare only the six exit frames when work stops. Active ticks merely select icons.
            settling = BuildSettleFrames(prior, RestFrame(), size);
            tray.Text = "Optimisarr Sidecar — click for activity";
            timer.Start();
        }
        else Paint();
    }

    private void Paint()
    {
        if (working && animationEnabled)
            tray.Icon = ActiveFrames()[ActivityIconFrame.Index(true, true, elapsed.Elapsed)];
        else if (settling is not null)
        {
            var index = ActivityIconFrame.SettleIndex(elapsed.Elapsed);
            if (index < ActivityIconFrame.SettleFrames) tray.Icon = settling[index];
            else
            {
                tray.Icon = RestFrame();
                timer.Stop();
                DisposeSettling();
            }
        }
        else tray.Icon = RestFrame();
        tray.Text = working ? "Optimisarr Sidecar — working" : "Optimisarr Sidecar — click for activity";
    }

    private Icon[] ActiveFrames() => lightSystemTheme ? lightFrames : darkFrames;
    private Icon RestFrame() => ActiveFrames()[0];

    private static bool IsLightSystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private static Bitmap LoadBitmap(string name)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Resources/{name}")).Stream;
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }

    private static Icon[] BuildFrames(Bitmap sheet, Icon idle, Size size)
    {
        var frames = new Icon[ActivityIconFrame.Count];
        frames[0] = idle;
        for (var index = 1; index < frames.Length; index++)
        {
            using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = QualityGraphics(frame))
                graphics.DrawImage(sheet, new Rectangle(Point.Empty, size),
                    new Rectangle(index % 11 * 48, index / 11 * 48, 48, 48), GraphicsUnit.Pixel);
            frames[index] = IconFromBitmap(frame);
        }
        return frames;
    }

    internal static void RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        using var sourceStream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/AppIcon.ico")).Stream;
        using var source = new Icon(sourceStream);
        using var dark = LoadBitmap("BrandMotion.png");
        using var light = LoadBitmap("BrandMotionLight.png");
        using var lightIdle = LoadBitmap("BrandMarkLight.png");
        var size = Forms.SystemInformation.SmallIconSize;
        using var lightMark = DrawIcon(lightIdle, size);
        foreach (var (theme, frames) in new[] {
            ("dark", BuildFrames(dark, new Icon(source, size), size)),
            ("light", BuildFrames(light, IconFromBitmap(lightMark), size)) })
        {
            try
            {
                foreach (var index in new[] { 0, 12, 32, 49, 72, 87 })
                {
                    using var bitmap = frames[index].ToBitmap();
                    bitmap.Save(Path.Combine(directory, $"{theme}-{index:D2}.png"), ImageFormat.Png);
                }
                var exits = BuildSettleFrames(frames[32], frames[0], size);
                try
                {
                    for (var index = 0; index < exits.Length; index++)
                    {
                        using var bitmap = exits[index].ToBitmap();
                        bitmap.Save(Path.Combine(directory, $"{theme}-settle-{index:D2}.png"), ImageFormat.Png);
                    }
                }
                finally { foreach (var frame in exits) frame.Dispose(); }
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }

    private static Icon[] BuildSettleFrames(Icon from, Icon to, Size size)
    {
        using var start = from.ToBitmap();
        using var finish = to.ToBitmap();
        var frames = new Icon[ActivityIconFrame.SettleFrames];
        for (var index = 0; index < frames.Length; index++)
        {
            using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = QualityGraphics(frame))
            {
                graphics.DrawImage(finish, new Rectangle(Point.Empty, size));
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 1f - (index + 1f) / frames.Length });
                graphics.DrawImage(start, new Rectangle(Point.Empty, size), 0, 0,
                    start.Width, start.Height, GraphicsUnit.Pixel, attributes);
            }
            frames[index] = IconFromBitmap(frame);
        }
        return frames;
    }

    private static Bitmap DrawIcon(Bitmap artwork, Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = QualityGraphics(bitmap);
        graphics.DrawImage(artwork, new Rectangle(Point.Empty, size));
        return bitmap;
    }

    private static Graphics QualityGraphics(Bitmap bitmap)
    {
        var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        return graphics;
    }

    private static Icon IconFromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private void DisposeSettling()
    {
        if (settling is null) return;
        foreach (var frame in settling) frame.Dispose();
        settling = null;
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        timer.Stop();
        tray.Visible = false;
        tray.Icon = null;
        DisposeSettling();
        foreach (var frame in darkFrames) frame.Dispose();
        foreach (var frame in lightFrames) frame.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
