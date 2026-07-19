namespace Optimisarr.Core.Queue;

/// <summary>
/// Decides which of a file's audio tracks a kept-languages rule removes. Pure and
/// deterministic so the safety behaviour is fully unit tested: a track with an
/// unknown language is never removed, and when no track matches a kept language
/// nothing is removed — so a file that had audio always keeps at least one track.
/// </summary>
public static class AudioTrackSelection
{
    /// <summary>
    /// Returns the audio-relative stream indexes to remove: every track whose language is
    /// known and matches none of <paramref name="keepLanguages"/>. An empty keep list keeps
    /// everything, as does a file where no track matches a kept language.
    /// </summary>
    public static IReadOnlyList<int> SelectRemovals(
        IReadOnlyList<string?> trackLanguages,
        IReadOnlyList<string> keepLanguages)
    {
        if (keepLanguages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var kept = keepLanguages
            .Select(language => TrackLanguages.TryCanonicaliseKnown(language, out var canonical)
                ? canonical
                : null)
            .Where(language => language is not null)
            .Select(language => language!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (kept.Count == 0)
        {
            return Array.Empty<int>();
        }

        var removals = new List<int>();
        var anyKeptMatch = false;
        for (var index = 0; index < trackLanguages.Count; index++)
        {
            if (!TrackLanguages.TryCanonicaliseKnown(trackLanguages[index], out var trackLanguage))
            {
                continue;
            }

            if (kept.Contains(trackLanguage))
            {
                anyKeptMatch = true;
            }
            else
            {
                removals.Add(index);
            }
        }

        return anyKeptMatch ? removals : Array.Empty<int>();
    }
}
