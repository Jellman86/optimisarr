namespace Optimisarr.Core.Queue;

/// <summary>
/// Decides which of a file's audio tracks a kept-languages rule removes. Pure and
/// deterministic so the safety behaviour is fully unit tested: a track with an
/// unknown language is never removed, and when no track matches a kept language
/// nothing is removed — so a file that had audio always keeps at least one track.
/// </summary>
public static class AudioTrackSelection
{
    // Tags that mean "language not identified" (und), "no linguistic content" (zxx, e.g. an
    // instrumental score), or "multiple languages" (mul). None of them can prove a track is
    // unwanted, so such tracks are always kept.
    private static readonly HashSet<string> UnknownTags =
        new(StringComparer.OrdinalIgnoreCase) { "und", "unk", "zxx", "mul" };

    // ISO 639 spellings of the same language: two-letter 639-1 codes and, where ISO 639-2
    // defines both, the bibliographic (B) form — containers commonly tag with B (e.g. "ger",
    // "fre") while operators may type the two-letter or terminological form. Each alias maps
    // to one canonical spelling; codes not listed here compare by exact (case-insensitive) text.
    private static readonly Dictionary<string, string> CanonicalCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng",
        ["fr"] = "fra", ["fre"] = "fra",
        ["de"] = "deu", ["ger"] = "deu",
        ["es"] = "spa",
        ["it"] = "ita",
        ["ja"] = "jpn",
        ["zh"] = "zho", ["chi"] = "zho",
        ["ko"] = "kor",
        ["ru"] = "rus",
        ["pt"] = "por",
        ["nl"] = "nld", ["dut"] = "nld",
        ["sv"] = "swe",
        ["no"] = "nor",
        ["da"] = "dan",
        ["fi"] = "fin",
        ["pl"] = "pol",
        ["cs"] = "ces", ["cze"] = "ces",
        ["sk"] = "slk", ["slo"] = "slk",
        ["hu"] = "hun",
        ["el"] = "ell", ["gre"] = "ell",
        ["tr"] = "tur",
        ["ar"] = "ara",
        ["he"] = "heb",
        ["hi"] = "hin",
        ["ta"] = "tam",
        ["te"] = "tel",
        ["th"] = "tha",
        ["vi"] = "vie",
        ["id"] = "ind",
        ["ms"] = "msa", ["may"] = "msa",
        ["uk"] = "ukr",
        ["ro"] = "ron", ["rum"] = "ron",
        ["bg"] = "bul",
        ["hr"] = "hrv",
        ["sr"] = "srp",
        ["sl"] = "slv",
        ["ca"] = "cat",
        ["is"] = "isl", ["ice"] = "isl",
        ["fa"] = "fas", ["per"] = "fas",
        ["bn"] = "ben",
        ["ur"] = "urd",
        ["et"] = "est",
        ["lv"] = "lav",
        ["lt"] = "lit",
        ["eu"] = "eus", ["baq"] = "eus",
        ["gl"] = "glg",
        ["cy"] = "cym", ["wel"] = "cym",
        ["mk"] = "mkd", ["mac"] = "mkd",
        ["sq"] = "sqi", ["alb"] = "sqi",
        ["hy"] = "hye", ["arm"] = "hye",
        ["ka"] = "kat", ["geo"] = "kat",
        ["my"] = "mya", ["bur"] = "mya",
        ["bo"] = "bod", ["tib"] = "bod"
    };

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

        var kept = keepLanguages.Select(Canonicalise).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removals = new List<int>();
        var anyKeptMatch = false;
        for (var index = 0; index < trackLanguages.Count; index++)
        {
            if (IsUnknown(trackLanguages[index]))
            {
                continue;
            }

            if (kept.Contains(Canonicalise(trackLanguages[index]!)))
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

    /// <summary>
    /// Parses a stored comma-separated language list (e.g. "eng, jpn") into distinct
    /// normalised codes. Null/blank input parses to an empty list, meaning "keep everything".
    /// </summary>
    public static IReadOnlyList<string> ParseLanguageList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.ToLowerInvariant())
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Parses a stored per-track language summary (e.g. "eng, jpn, und") back into positional
    /// tags: index = audio-relative stream index, so entries are never deduplicated or dropped.
    /// Null/blank input means the languages were never captured and returns <c>null</c>.
    /// </summary>
    public static IReadOnlyList<string?>? ParseTrackLanguages(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(tag => string.IsNullOrEmpty(tag) ? null : tag)
            .ToArray();
    }

    private static bool IsUnknown(string? tag) =>
        string.IsNullOrWhiteSpace(tag) || UnknownTags.Contains(tag.Trim());

    private static string Canonicalise(string code)
    {
        var trimmed = code.Trim();
        return CanonicalCodes.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }
}
