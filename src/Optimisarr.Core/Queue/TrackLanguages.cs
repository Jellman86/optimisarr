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

    // The complete set of individual ISO 639-2 language identifiers published by the Library of
    // Congress. Collective identifiers (for example "afa"), special values (und/mul/mis/zxx),
    // and the qaa-qtz private-use range are deliberately absent: none identifies one language
    // strongly enough to authorise destructive track removal. Future or unfamiliar identifiers
    // therefore fail closed and the associated track is retained.
    private const string IndividualTerminologyCodes = """
        aar abk ace ach ada ady afh afr ain aka akk ale alt amh ang anp ara arc arg arn arp arw asm ast
        ava ave awa aym aze bak bal bam ban bas bej bel bem ben bho bik bin bis bla bod bos bra bre bua
        bug bul byn cad car cat ceb ces cha chb che chg chk chm chn cho chp chr chu chv chy cnr cop cor
        cos cre crh csb cym dak dan dar del den deu dgr din div doi dsb dua dum dyu dzo efi egy eka ell
        elx eng enm epo est eus ewe ewo fan fao fas fat fij fil fin fon fra frm fro frr frs fry ful fur
        gaa gay gba gez gil gla gle glg glv gmh goh gon gor got grb grc grn gsw guj gwi hai hat hau haw
        heb her hil hin hit hmn hmo hrv hsb hun hup hye iba ibo ido iii iku ile ilo ina ind inh ipk isl
        ita jav jbo jpn jpr jrb kaa kab kac kal kam kan kas kat kau kaw kaz kbd kha khm kho kik kin kir
        kmb kok kom kon kor kos kpe krc krl kru kua kum kur kut lad lah lam lao lat lav lez lim lin lit
        lol loz ltz lua lub lug lui lun luo lus mad mag mah mai mak mal man mar mas mdf mdr men mga mic
        min mkd mlg mlt mnc mni moh mon mos mri msa mus mwl mwr mya myv nap nau nav nbl nde ndo nds nep
        new nia niu nld nno nob nog non nor nqo nso nwc nya nym nyn nyo nzi oci oji ori orm osa oss ota
        pag pal pam pan pap pau peo phn pli pol pon por pro pus que raj rap rar roh rom ron run rup rus sad
        sag sah sam san sas sat scn sco sel sga shn sid sin slk slv sma sme smj smn smo sms sna snd snk
        sog som sot spa sqi srd srn srp srr ssw suk sun sus sux swa swe syc syr tah tam tat tel tem ter tet
        tgk tgl tha tig tir tiv tkl tlh tli tmh tog ton tpi tsi tsn tso tuk tum tur tvl twi tyv udm uga
        uig ukr umb urd uzb vai ven vie vol vot wal war was wln wol xal xho yao yap yid yor zap zbl zen zgh
        zha zho zul zun zza
        """;

    // ISO 639-1 -> ISO 639-2/T plus the 20 ISO 639-2/B legacy spellings used by media
    // containers. Keeping these tables in-process makes behaviour deterministic and reviewable.
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
        var codes = IndividualTerminologyCodes.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(code => code, code => code, StringComparer.OrdinalIgnoreCase);

        var bibliographicAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        foreach (var (alias, canonical) in bibliographicAliases)
        {
            codes.Add(alias, canonical);
        }

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

        var rawCodes = SplitLanguageList(value);
        var canonical = new List<string>(rawCodes.Count);
        foreach (var code in rawCodes)
        {
            if (!TryCanonicaliseKnown(code, out var known))
            {
                // Treat the stored rule atomically. Silently dropping just the unrecognised part
                // would broaden deletion from the operator's original (possibly legacy) intent.
                return Array.Empty<string>();
            }

            if (!canonical.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                canonical.Add(known);
            }
        }

        return canonical;
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

        var rawCodes = SplitLanguageList(value);
        if (rawCodes.Count == 0)
        {
            return false;
        }

        var codes = new List<string>(rawCodes.Count);
        foreach (var code in rawCodes)
        {
            if (!TryCanonicaliseKnown(code, out var canonical))
            {
                return false;
            }

            if (!codes.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                codes.Add(canonical);
            }
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
        return !TryCanonicaliseKnown(tag, out _);
    }

    /// <summary>Maps a two-letter or bibliographic spelling to its canonical ISO 639 code.</summary>
    public static string Canonicalise(string code)
    {
        var trimmed = code.Trim().ToLowerInvariant();
        return TryCanonicaliseKnown(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>
    /// Resolves only a positively registered, individual ISO 639 language identifier to its
    /// ISO 639-2/T form. False means the caller has no authority to remove the associated data.
    /// </summary>
    public static bool TryCanonicaliseKnown(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var code = value.Trim();
        return CanonicalCodes.TryGetValue(code, out canonical!);
    }

    private static IReadOnlyList<string> SplitLanguageList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
