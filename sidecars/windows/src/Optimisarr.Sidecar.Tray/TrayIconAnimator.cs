using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Optimisarr.Sidecar.Tray;

internal sealed class TrayIconAnimator : IDisposable
{
    private readonly Forms.NotifyIcon tray;
    private readonly Icon[] frames;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch elapsed = new();
    private readonly bool animationEnabled;
    private bool working;

    internal TrayIconAnimator(Forms.NotifyIcon tray, Icon source, bool animationEnabled)
    {
        this.tray = tray;
        this.animationEnabled = animationEnabled;
        frames = BuildFrames(source, Forms.SystemInformation.SmallIconSize);
        tray.Icon = frames[0];
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000d / 6) };
        timer.Tick += (_, _) => Paint();
    }

    internal void SetWorking(bool next)
    {
        if (working == next) return;
        working = next;
        elapsed.Restart();
        if (working && animationEnabled) timer.Start();
        else timer.Stop();
        Paint();
    }

    private void Paint()
    {
        tray.Icon = frames[ActivityIconFrame.Index(working, animationEnabled, elapsed.Elapsed)];
        tray.Text = working ? "Optimisarr Sidecar — working" : "Optimisarr Sidecar — click for activity";
    }

    private static Icon[] BuildFrames(Icon source, Size size)
    {
        using var artwork = source.ToBitmap();
        var frames = new Icon[ActivityIconFrame.Count];
        frames[0] = new Icon(source, size);
        for (var index = 1; index < frames.Length; index++)
        {
            using var frame = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(size.Width / 2f, size.Height / 2f);
                graphics.RotateTransform(index * 360f / frames.Length);
                graphics.TranslateTransform(-size.Width / 2f, -size.Height / 2f);
                graphics.DrawImage(artwork, new Rectangle(Point.Empty, size));
            }
            var handle = frame.GetHicon();
            try
            {
                using var borrowed = Icon.FromHandle(handle);
                frames[index] = (Icon)borrowed.Clone();
            }
            finally { DestroyIcon(handle); }
        }
        return frames;
    }

    public void Dispose()
    {
        timer.Stop();
        tray.Visible = false;
        tray.Icon = null;
        foreach (var frame in frames) frame.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
