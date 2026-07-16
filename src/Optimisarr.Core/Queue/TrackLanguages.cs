namespace Optimisarr.Core.Queue;

/// <summary>
/// Shared ISO 639 language handling for track-removal rules: parsing stored language
/// lists, canonicalising B/T and two-letter spellings, and recognising tags that mean
/// "language unknown" (which never prove a track is safe to remove). Used by both
/// <see cref="AudioTrackSelection"/> and <see cref="SubtitleTrackSelection"/>.
/// </summary>
public static class TrackLanguages
{
    // Tags that mean "language not identified" (und), "no linguistic content" (zxx, e.g. an
    // instrumental score), "multiple languages" (mul), or "uncoded" (mis). None of them can
    // prove a track is unwanted, so such tracks are always kept.
    private static readonly HashSet<string> UnknownTags =
        new(StringComparer.OrdinalIgnoreCase) { "mis", "mul", "und", "unk", "zxx" };

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
    /// tags: index = track-relative stream index, so entries are never deduplicated or dropped.
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

    /// <summary>
    /// True when a tag cannot prove which language a track carries: blank, an explicit
    /// unknown code, a shape that is not a 2/3-letter ISO 639 code, or a private-use
    /// (qaa–qtz) code with no portable meaning. Such tracks are never safe to remove.
    /// </summary>
    public static bool IsUnknown(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return true;
        }

        var code = tag.Trim().ToLowerInvariant();
        if (UnknownTags.Contains(code) || !IsAsciiLanguageCode(code))
        {
            return true;
        }

        return code.Length == 3
            && string.CompareOrdinal(code, "qaa") >= 0
            && string.CompareOrdinal(code, "qtz") <= 0;
    }

    /// <summary>Maps a two-letter or bibliographic spelling to its canonical ISO 639 code.</summary>
    public static string Canonicalise(string code)
    {
        var trimmed = code.Trim();
        return CanonicalCodes.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    private static bool IsAsciiLanguageCode(string value) =>
        value.Length is 2 or 3 && value.All(char.IsAsciiLetter);
}
