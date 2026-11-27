using System.Globalization;

namespace Optimisarr.Core.Library;

/// <param name="Name">The tag's name as the file's container gives it, trimmed.</param>
public sealed record AudioFormatTag(string Name, string Value);

/// <summary>
/// A music file's tags as ffprobe reports them, each under one name whatever its format calls it, so
/// the tags of a source and of a file converted to another format can be compared. A FLAC file's
/// <c>MUSICBRAINZ_ALBUMID</c>, an MP3's <c>TXXX:MusicBrainz Album Id</c> and an M4A's
/// <c>----:com.apple.iTunes:MusicBrainz Album Id</c> are one tag.
/// </summary>
public static class AudioFormatTags
{
    // Written again for every new file, so they are not music tags. iTunSMPB is iTunes' encoder delay
    // and length, TLEN an MP3's length in milliseconds, and Encoding Params and cdec name the encoder
    // and its settings; each is true only of the file it was written for.
    private static readonly HashSet<string> Technical =
    [
        "encoder", "vendor_id", "major_brand", "minor_version", "compatible_brands", "handler_name", "duration",
        "language", OptimisationMarker.MetadataKey, "itunsmpb", "tlen", "encoding params", "cdec",
    ];

    // The freeform names Picard and iTunes give these tags in an M4A, keyed by the name the tag has here.
    private static readonly Dictionary<string, string> FreeformNames = new[]
    {
        "MusicBrainz Track Id", "MusicBrainz Release Track Id", "MusicBrainz Album Id", "MusicBrainz Artist Id",
        "MusicBrainz Album Artist Id", "MusicBrainz Release Group Id", "MusicBrainz Work Id", "MusicBrainz Disc Id",
        "MusicBrainz Original Album Id", "MusicBrainz Original Artist Id", "MusicBrainz Album Status", "MusicBrainz Album Type",
        "MusicBrainz Album Release Country", "Acoustid Id", "Acoustid Fingerprint", "MusicIP PUID", "BPM",
    }.ToDictionary(name => name.ToLowerInvariant(), name => name, StringComparer.Ordinal);

    // The M4A sort-order atoms, keyed by the name ffprobe reads each back as.
    private static readonly Dictionary<string, string> SortAtoms = new(StringComparer.Ordinal)
    {
        ["sort_name"] = "sonm", ["sort_artist"] = "soar", ["sort_album"] = "soal",
        ["sort_album_artist"] = "soaa", ["sort_composer"] = "soco",
    };

    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["albumartist"] = "album_artist", ["year"] = "date",
            ["totaltracks"] = "tracktotal", ["totaldiscs"] = "disctotal",
            // Vorbis comments, then FFmpeg's names for ID3 frames.
            ["titlesort"] = "sort_name", ["title-sort"] = "sort_name",
            ["artistsort"] = "sort_artist", ["artist-sort"] = "sort_artist",
            ["albumsort"] = "sort_album", ["album-sort"] = "sort_album",
            ["albumartistsort"] = "sort_album_artist", ["album_artist-sort"] = "sort_album_artist", ["tso2"] = "sort_album_artist",
            ["composersort"] = "sort_composer", ["composer-sort"] = "sort_composer", ["tsoc"] = "sort_composer",
            ["tbpm"] = "bpm",
            ["releasestatus"] = "musicbrainz album status", ["releasetype"] = "musicbrainz album type",
            ["releasecountry"] = "musicbrainz album release country",
            ["acoustid_id"] = "acoustid id", ["acoustid_fingerprint"] = "acoustid fingerprint", ["musicip_puid"] = "musicip puid",
        };
        // Vorbis spells the MusicBrainz identifiers MUSICBRAINZ_ALBUMID where ID3 and M4A say "MusicBrainz Album Id".
        foreach (var name in FreeformNames.Keys.Where(name => name.StartsWith("musicbrainz ", StringComparison.Ordinal) && name.EndsWith(" id", StringComparison.Ordinal)))
            aliases["musicbrainz_" + name["musicbrainz ".Length..^" id".Length].Replace(" ", "", StringComparison.Ordinal) + "id"] = name;
        return aliases;
    }

    /// <summary>The conventional M4A freeform name for a tag under this name, or null when it has none.</summary>
    public static string? Mp4FreeformName(string name) => FreeformNames.GetValueOrDefault(name);

    /// <summary>The M4A atom a sort-order tag under this name is written as, or null when it is not one.</summary>
    public static string? Mp4SortAtom(string name) => SortAtoms.GetValueOrDefault(name);

    /// <summary>
    /// The tags under one name each, without those written for every new file. A <c>language</c>
    /// is left out too: ffprobe's tags hold the audio track's language with the file's own, so it is
    /// compared from <see cref="MediaProbeResult.TagLanguage"/> instead.
    /// </summary>
    public static IReadOnlyDictionary<string, AudioFormatTag> Normalise(IReadOnlyDictionary<string, string>? tags)
    {
        var result = new Dictionary<string, AudioFormatTag>(StringComparer.Ordinal);
        if (tags is null) return result;
        foreach (var (rawKey, rawValue) in tags.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var spelled = rawKey.Trim().ToLowerInvariant();
            if (Technical.Contains(spelled) || string.IsNullOrWhiteSpace(rawValue)) continue;
            var tag = new AudioFormatTag(rawKey.Trim(), rawValue.Trim());
            var name = Aliases.GetValueOrDefault(spelled, spelled);
            if (result.TryGetValue(name, out var other) && other.Value != tag.Value)
            {
                // Two spellings that disagree are two tags, each to be kept under its own name.
                if (spelled == name) result[other.Name.ToLowerInvariant()] = other;
                else name = spelled;
            }
            result[name] = tag;
        }
        FoldTotal(result, "track", "tracktotal");
        FoldTotal(result, "disc", "disctotal");
        return result;
    }

    /// <summary>
    /// A track or disc number as "number/total" whichever way its format stores the total, with the
    /// numbers written plainly, so "03" with a total of "12" and "3/12" are one value.
    /// </summary>
    private static void FoldTotal(Dictionary<string, AudioFormatTag> tags, string numberName, string totalName)
    {
        if (!tags.TryGetValue(numberName, out var number)) return;
        var parts = number.Value.Split('/');
        if (parts.Length > 2 || !TryNumber(parts[0], out var position)) return;
        int? total = null;
        if (parts.Length == 2)
        {
            if (!TryNumber(parts[1], out var stated)) return;
            total = stated;
        }
        if (tags.TryGetValue(totalName, out var separate) && TryNumber(separate.Value, out var separateTotal) && (total ?? separateTotal) == separateTotal)
        {
            total = separateTotal;
            tags.Remove(totalName);
        }
        tags[numberName] = number with
        {
            Value = total is { } known and > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{position}/{known}")
                : position.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
