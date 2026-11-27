using System.Globalization;

namespace Optimisarr.Core.Rules;

public enum TrackSavingKnowledge
{
    /// <summary>Every removed track has a recorded or estimated size.</summary>
    Known = 0,

    /// <summary>The file was probed before track sizes were recorded.</summary>
    NotRecorded = 1,

    /// <summary>A removed track reports neither a size nor a bitrate to estimate one from.</summary>
    Unknown = 2
}

/// <summary>
/// How much removing a file's unwanted tracks would save: the sum of the removed tracks' sizes,
/// and that as a share of the file. Only an estimate of the new file's size — the container's own
/// overhead changes a little too — so it decides whether a rewrite is worth doing, never whether
/// one succeeded; the size verification gates still judge the written file.
/// </summary>
public sealed record TrackRemovalSaving(long? Bytes, double? Percent, TrackSavingKnowledge Knowledge)
{
    public static TrackRemovalSaving Measure(
        long fileSizeBytes,
        IReadOnlyList<long?>? audioTrackBytes,
        IReadOnlyList<int> audioRemovals,
        IReadOnlyList<long?>? subtitleTrackBytes,
        IReadOnlyList<int> subtitleRemovals)
    {
        if ((audioRemovals.Count > 0 && audioTrackBytes is null)
            || (subtitleRemovals.Count > 0 && subtitleTrackBytes is null))
        {
            return new TrackRemovalSaving(null, null, TrackSavingKnowledge.NotRecorded);
        }

        long total = 0;
        foreach (var size in RemovedSizes(audioTrackBytes, audioRemovals).Concat(RemovedSizes(subtitleTrackBytes, subtitleRemovals)))
        {
            if (size is not { } bytes)
            {
                return new TrackRemovalSaving(null, null, TrackSavingKnowledge.Unknown);
            }

            total += bytes;
        }

        // Removing tracks always leaves something behind, so sizes adding up to the whole file or
        // more are not this file's.
        if (fileSizeBytes > 0 && total >= fileSizeBytes)
        {
            return new TrackRemovalSaving(null, null, TrackSavingKnowledge.Unknown);
        }

        double? percent = fileSizeBytes > 0 ? total * 100.0 / fileSizeBytes : null;
        return new TrackRemovalSaving(total, percent, TrackSavingKnowledge.Known);
    }

    // A removal index past the end of the sizes means they were recorded for another track list,
    // so that track's size is not known.
    private static IEnumerable<long?> RemovedSizes(IReadOnlyList<long?>? sizes, IReadOnlyList<int> removals) =>
        removals.Select(index => sizes is not null && index >= 0 && index < sizes.Count ? sizes[index] : null);

    /// <summary>"about 120 MB (0.2%)". <paramref name="roundDown"/> for a saving that falls short of a
    /// minimum, so rounding never prints it as reaching the minimum it fails.</summary>
    public string Describe(bool roundDown = false) =>
        Percent is { } percent
            ? $"about {TrackSavingMinimum.FormatSize(Bytes ?? 0, roundDown)} ({TrackSavingMinimum.FormatPercent(percent, roundDown)})"
            : $"about {TrackSavingMinimum.FormatSize(Bytes ?? 0, roundDown)}";
}

/// <summary>
/// A library's minimum saving for a rewrite that only removes tracks. Either part may be unset;
/// when both are set a file must meet both, so neither a small share of a huge file nor a large
/// share of a tiny one is rewritten. Unset entirely, nothing is refused.
/// </summary>
public sealed record TrackSavingMinimum(double? Percent, long? Bytes)
{
    public bool IsSet => Percent is not null || Bytes is not null;

    /// <summary>Zero would be no minimum, and a saving of the whole file is not a track removal.</summary>
    public static bool ValidPercent(double percent) => double.IsFinite(percent) && percent > 0 && percent <= 99;

    public static bool ValidBytes(long bytes) => bytes > 0;

    public const string RangeError =
        "The minimum track-removal saving must be greater than 0% and at most 99%, and its size greater than zero, or blank for none.";

    public static TrackSavingMinimum Of(RuleSettings rules) =>
        new(rules.MinimumTrackRemovalSavingPercent, rules.MinimumTrackRemovalSavingBytes);

    /// <summary>
    /// True or false when a minimum is set and the saving is known; null when there is no minimum
    /// to meet or the saving cannot be worked out.
    /// </summary>
    public bool? IsMetBy(TrackRemovalSaving saving)
    {
        if (!IsSet || saving.Knowledge != TrackSavingKnowledge.Known)
        {
            return null;
        }

        var bytes = saving.Bytes ?? 0;
        return (Percent is not { } percent || (saving.Percent ?? 0) >= percent)
            && (Bytes is not { } minimumBytes || bytes >= minimumBytes);
    }

    /// <summary>Why the rewrite is not worth doing, or null when it is (or no minimum is set).</summary>
    public string? Refusal(TrackRemovalSaving saving)
    {
        if (!IsSet)
        {
            return null;
        }

        return saving.Knowledge switch
        {
            TrackSavingKnowledge.NotRecorded =>
                "Track sizes not recorded yet — re-probe the file to check this library's minimum saving",
            TrackSavingKnowledge.Unknown =>
                "The size of the tracks to remove is not recorded in this file and cannot be estimated, so this library's minimum saving cannot be checked",
            _ => IsMetBy(saving) == true
                ? null
                : $"Removing these tracks would save {saving.Describe(roundDown: true)}, below this library's minimum of {Describe()}"
        };
    }

    /// <summary>"5%", "1 GB", or "5% and 1 GB".</summary>
    public string Describe() =>
        string.Join(" and ", new[]
        {
            Percent is { } percent ? FormatPercent(percent) : null,
            Bytes is { } bytes ? FormatSize(bytes) : null
        }.Where(part => part is not null));

    internal static string FormatPercent(double percent, bool roundDown = false)
    {
        var shown = roundDown ? RoundDown(percent, 2) : percent;
        return $"{shown.ToString(shown >= 1 ? "0.##" : "0.0#", CultureInfo.InvariantCulture)}%";
    }

    internal static string FormatSize(long bytes, bool roundDown = false)
    {
        const long megabyte = 1024L * 1024L;
        const long gigabyte = megabyte * 1024L;

        var (value, unit) = bytes >= gigabyte ? (bytes / (double)gigabyte, "GB") : (bytes / (double)megabyte, "MB");
        var shown = roundDown ? RoundDown(value, 1) : value;
        return $"{shown.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
    }

    // Through decimal, so a binary fraction just under a round number (0.29 held as 0.28999…) is
    // not shown a step lower than it is.
    private static double RoundDown(double value, int decimals)
    {
        var scale = (decimal)Math.Pow(10, decimals);
        return (double)(Math.Floor((decimal)value * scale) / scale);
    }
}
