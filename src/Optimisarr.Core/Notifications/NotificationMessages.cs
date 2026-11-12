namespace Optimisarr.Core.Notifications;

/// <summary>A rendered notification: a short title and a human-readable body.</summary>
public sealed record NotificationMessage(string Title, string Body);

/// <summary>
/// Pure renderers for the events Optimisarr notifies on. Sizes are passed in already
/// measured so the text is deterministic and unit tested — no clock, no I/O.
/// </summary>
public static class NotificationMessages
{
    public static NotificationMessage ReplacementCompleted(string path, long originalBytes, long newBytes) =>
        new("Optimisarr: replaced a file", $"{path}\n{SizeChange(originalBytes, newBytes)}");

    // A file that grew is reported as growth: "Saved -190 MB" reads as a saving to anyone skimming.
    // A size of 0 means it could not be measured, so there is nothing to compare: only the sizes
    // are given, never a saving.
    private static string SizeChange(long originalBytes, long newBytes)
    {
        var sizes = $"{Humanize(originalBytes)} → {Humanize(newBytes)}";
        if (originalBytes <= 0 || newBytes <= 0) return $"Size: {sizes}.";
        if (newBytes == originalBytes) return $"Size unchanged: {sizes}.";
        var change = Math.Abs(newBytes - originalBytes);
        var percent = Percent(100.0 * change / originalBytes, saving: newBytes < originalBytes);
        return newBytes < originalBytes
            ? $"Saved {Humanize(change)} ({percent}): {sizes}."
            : $"Grew by {Humanize(change)} ({percent}): {sizes}.";
    }

    // Whole percents from 1% up; below that a tenth, and never 0% for a change that happened.
    // A saving whose file still exists is never the whole file, so it stops at 99.9%.
    private static string Percent(double percent, bool saving)
    {
        var shown = percent >= 1 ? Math.Round(percent) : Math.Max(0.1, Math.Round(percent, 1));
        if (saving && percent >= 99.5) shown = Math.Min(99.9, Math.Round(percent, 1));
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#}%", shown);
    }

    public static NotificationMessage JobFailed(string path, string error) =>
        new("Optimisarr: job failed", $"{path}\n{error}");

    public static NotificationMessage Test() =>
        new("Optimisarr: test notification", "Your notification target is working.");

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
