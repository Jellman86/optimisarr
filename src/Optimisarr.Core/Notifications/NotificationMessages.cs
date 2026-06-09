namespace Optimisarr.Core.Notifications;

/// <summary>A rendered notification: a short title and a human-readable body.</summary>
public sealed record NotificationMessage(string Title, string Body);

/// <summary>
/// Pure renderers for the events Optimisarr notifies on. Sizes are passed in already
/// measured so the text is deterministic and unit tested — no clock, no I/O.
/// </summary>
public static class NotificationMessages
{
    public static NotificationMessage ReplacementCompleted(string path, long originalBytes, long newBytes)
    {
        var saved = originalBytes - newBytes;
        var percent = originalBytes > 0 ? (int)Math.Round(100.0 * saved / originalBytes) : 0;
        return new NotificationMessage(
            "Optimisarr: replaced a file",
            $"{path}\nSaved {Humanize(saved)} ({percent}%): {Humanize(originalBytes)} → {Humanize(newBytes)}.");
    }

    public static NotificationMessage JobFailed(string path, string error) =>
        new("Optimisarr: job failed", $"{path}\n{error}");

    internal static string Humanize(long bytes)
    {
        var negative = bytes < 0;
        double value = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var formatted = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} {1}", value, units[unit]);
        return negative ? $"-{formatted}" : formatted;
    }
}
