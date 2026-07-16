namespace Optimisarr.Core.Queue;

/// <summary>
/// Shared ISO 639 language handling for track-removal rules: parsing stored language
/// lists, canonicalising B/T and two-letter spellings, and recognising tags that mean
/// "language unknown" (which never prove a track is safe to remove). Used by both
/// <see cref="AudioTrackSelection"/> and <see cref="SubtitleTrackSelection"/>.
/// </summary>
public static class TrackLanguages
{
    public const int MaxLanguageListLength = 256;

    // Tags that mean "language not identified" (und), "no linguistic content" (zxx, e.g. an
    // instrumental score), or "multiple languages" (mul). None of them can prove a track is
    // unwanted, so such tracks are always kept.
    private static readonly HashSet<string> UnknownTags =
        new(StringComparer.OrdinalIgnoreCase) { "mis", "mul", "und", "unk", "zxx" };

    // The complete ISO 639-1 -> ISO 639-2 terminology mapping published by the Library of Congress,
    // plus all 20 legacy bibliographic spellings that containers still commonly carry. Keeping the
    // table in-process makes matching deterministic even when .NET runs without platform culture
    // data. Unrecognised three-letter codes compare exactly, preserving ISO 639-3 support.
    private static readonly IReadOnlyDictionary<string, string> CanonicalCodes = BuildCanonicalCodes();

    private const string Alpha2ToTerminology = """
        aa:aar ab:abk af:afr ak:aka sq:sqi am:amh ar:ara an:arg hy:hye as:asm av:ava ae:ave
        ay:aym az:aze ba:bak bm:bam eu:eus be:bel bn:ben bi:bis bs:bos br:bre bg:bul my:mya
        ca:cat ch:cha ce:che zh:zho cu:chu cv:chv kw:cor co:cos cr:cre cs:ces da:dan dv:div
        nl:nld dz:dzo en:eng eo:epo et:est ee:ewe fo:fao fj:fij fi:fin fr:fra fy:fry ff:ful
        ka:kat de:deu gd:gla ga:gle gl:glg gv:glv el:ell gn:grn gu:guj ht:hat ha:hau he:heb
        hz:her hi:hin ho:hmo hr:hrv hu:hun ig:ibo is:isl io:ido ii:iii iu:iku ie:ile ia:ina
        id:ind ik:ipk it:ita jv:jav ja:jpn kl:kal kn:kan ks:kas kr:kau kk:kaz km:khm ki:kik
        rw:kin ky:kir kv:kom kg:kon ko:kor kj:kua ku:kur lo:lao la:lat lv:lav li:lim ln:lin
        lt:lit lb:ltz lu:lub lg:lug mk:mkd mh:mah ml:mal mi:mri mr:mar ms:msa mg:mlg mt:mlt
        mn:mon na:nau nv:nav nr:nbl nd:nde ng:ndo ne:nep nn:nno nb:nob no:nor ny:nya oc:oci
        oj:oji or:ori om:orm os:oss pa:pan fa:fas pi:pli pl:pol pt:por ps:pus qu:que rm:roh
        ro:ron rn:run ru:rus sg:sag sa:san si:sin sk:slk sl:slv se:sme sm:smo sn:sna sd:snd
        so:som st:sot es:spa sc:srd sr:srp ss:ssw su:sun sw:swa sv:swe ty:tah ta:tam tt:tat
        te:tel tg:tgk tl:tgl th:tha bo:bod ti:tir to:ton tn:tsn ts:tso tk:tuk tr:tur tw:twi
        ug:uig uk:ukr ur:urd uz:uzb ve:ven vi:vie vo:vol cy:cym wa:wln wo:wol xh:xho yi:yid
        yo:yor za:zha zu:zul
        """;

    private static Dictionary<string, string> BuildCanonicalCodes()
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alb"] = "sqi",
            ["arm"] = "hye",
            ["baq"] = "eus",
            ["bur"] = "mya",
            ["chi"] = "zho",
            ["cze"] = "ces",
            ["dut"] = "nld",
            ["fre"] = "fra",
            ["geo"] = "kat",
            ["ger"] = "deu",
            ["gre"] = "ell",
            ["ice"] = "isl",
            ["mac"] = "mkd",
            ["mao"] = "mri",
            ["may"] = "msa",
            ["per"] = "fas",
            ["rum"] = "ron",
            ["slo"] = "slk",
            ["tib"] = "bod",
            ["wel"] = "cym"
        };

        foreach (var pair in Alpha2ToTerminology.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            codes.Add(pair[..2], pair[3..]);
        }

        return codes;
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
    /// Validates and canonicalises an operator-supplied kept-language list for persistence.
    /// Blank means keep every track; otherwise every distinct entry must be a two- or three-letter
    /// language code and the formatted value must fit the database column.
    /// </summary>
    public static bool TryNormaliseLanguageList(string? value, out string? normalised)
    {
        normalised = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var codes = ParseLanguageList(value);
        if (codes.Count == 0 || codes.Any(code => !IsAsciiLanguageCode(code)))
        {
            return false;
        }

        var formatted = string.Join(", ", codes);
        if (formatted.Length > MaxLanguageListLength)
        {
            return false;
        }

        normalised = formatted;
        return true;
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

        // ISO 639-2 reserves qaa-qtz for private local use. Such a tag has no portable meaning,
        // so it cannot prove that a track is safe to delete.
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
