namespace Optimisarr.Core.Queue;

/// <summary>
/// Decides which of a file's subtitle tracks a kept-languages rule removes. A track
/// with an unknown language is never removed. Unlike <see cref="AudioTrackSelection"/>
/// there is no keep-at-least-one guard: subtitles are optional streams, so a file whose
/// subtitles are all in non-kept languages ends up with none — a normal state for media.
/// </summary>
public static class SubtitleTrackSelection
{
    /// <summary>
    /// Returns the subtitle-relative stream indexes to remove: every track whose
    /// language is known and matches none of <paramref name="keepLanguages"/>.
    /// An empty keep list keeps everything.
    /// </summary>
    public static IReadOnlyList<int> SelectRemovals(
        IReadOnlyList<string?> trackLanguages,
        IReadOnlyList<string> keepLanguages)
    {
        if (keepLanguages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var kept = keepLanguages.Select(TrackLanguages.Canonicalise)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removals = new List<int>();
        for (var index = 0; index < trackLanguages.Count; index++)
        {
            if (TrackLanguages.IsUnknown(trackLanguages[index]))
            {
                continue;
            }

            if (!kept.Contains(TrackLanguages.Canonicalise(trackLanguages[index]!)))
            {
                removals.Add(index);
            }
        }

        return removals;
    }
}
