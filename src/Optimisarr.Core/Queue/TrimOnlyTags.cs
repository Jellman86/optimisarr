using Optimisarr.Core.Library;

namespace Optimisarr.Core.Queue;

/// <summary>Whether a trimmed file's tags are the source's own, and if not, what was lost or changed.</summary>
public sealed record TagComparison(bool Kept, string Detail);

/// <summary>
/// A file trimmed without converting keeps its tags exactly. FFmpeg reads the source's tags into its
/// own key list and writes them again, and some do not come back the same: with FFmpeg 7.1, an MP3
/// loses COMM, USLT, POPM, UFID, PCNT, SYLT and URL frames or turns them into TXXX, turns a 2.3 TCMP or
/// TSOP into TXXX, and replaces a numeric genre with its name; a FLAC or Opus file has repeated fields
/// joined into one, COMMENT renamed DESCRIPTION, chapters dropped, and APPLICATION and CUESHEET blocks
/// lost; a WAV file keeps only its LIST INFO entries. It also rewrites every picture's MIME type in
/// its own spelling, and writes all text in Unicode, so single-byte text in a local code page comes
/// back as different words. <see cref="Refusal"/> leaves such a file alone before any work;
/// <see cref="Compare"/> is the verification that refuses any loss or addition it did not foresee.
/// </summary>
public static class TrimOnlyTags
{
    // Text frames FFmpeg writes back as the same frame (its ff_id3v2_tags plus the version's own),
    // measured to round-trip with a single value. TDRL is left out: FFmpeg writes it as TDRC.
    private static readonly HashSet<string> CommonTextFrames =
    [
        "TALB", "TBPM", "TCOM", "TCON", "TCOP", "TDLY", "TENC", "TEXT", "TFLT", "TIT1", "TIT2", "TIT3",
        "TKEY", "TLAN", "TMED", "TOAL", "TOFN", "TOLY", "TOPE", "TOWN", "TPE1", "TPE2", "TPE3", "TPE4",
        "TPOS", "TPUB", "TRCK", "TRSN", "TRSO", "TSRC",
    ];

    private static readonly HashSet<string> Id3v23TextFrames = ["TDAT", "TIME", "TORY", "TRDA", "TSIZ", "TYER"];

    private static readonly HashSet<string> Id3v24TextFrames =
        ["TDEN", "TDOR", "TDRC", "TDTG", "TMOO", "TPRO", "TSOA", "TSOP", "TSOT", "TSST"];

    // The MIME types FFmpeg writes a picture with (ff_id3v2_mime_tags): it reads any other spelling
    // as one of these, image/jpg as image/jpeg, or drops the picture.
    private static readonly HashSet<string> PictureMimeTypes = ["image/gif", "image/jpeg", "image/png", "image/tiff", "image/bmp", "image/webp"];

    /// <summary>
    /// Written again for the new file, so their old values are not expected to survive: the encoder
    /// name, the length in milliseconds (TLEN) and iTunes' gapless figures (iTunSMPB), both true only
    /// of the untrimmed file, and Optimisarr's own marker.
    /// </summary>
    private static bool Regenerated(MusicTagLayout layout, string key) => layout switch
    {
        MusicTagLayout.Id3 => key is "TSSE" or "TLEN"
            || key.Equals("TXXX:" + OptimisationMarker.MetadataKey, StringComparison.OrdinalIgnoreCase)
            || key.Equals("TXXX:iTunSMPB", StringComparison.OrdinalIgnoreCase),
        MusicTagLayout.Wav => key is "INFO:ISFT",
        _ => key is "ENCODER" || key.Equals(OptimisationMarker.MetadataKey, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The longest refusal stored with a file.</summary>
    public const int MaximumRefusalLength = 512;

    // A tag's name in a refusal or a verification detail; six or eight of them stay well inside a refusal.
    private const int MaximumNameLength = 40;

    /// <summary>Why the file is left alone, or null when every tag it has is known to come back unchanged.</summary>
    public static string? Refusal(MusicTagRead read)
    {
        if (read.Tags is not { } tags)
        {
            var problem = read.Problem ?? "no detail";
            if (problem.Length > 200) problem = problem[..200] + "…";
            return $"Its tags could not be read in full ({problem}), so it is not trimmed without converting it.";
        }

        if (Named(tags.Layout, tags.Tags.Where(tag => tag.LegacyText)) is { } legacy)
            return $"Some of its tags are written in a legacy character set ({legacy}), which trimming without converting would rewrite, so it is left alone.";

        // FFmpeg keeps one value per key, and its keys ignore case, so a second frame or entry of the
        // same kind (or a TXXX description differing only in case) is lost.
        var repeated = tags.Layout is MusicTagLayout.Id3 or MusicTagLayout.Wav
            ? tags.Tags.GroupBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var lost = Named(tags.Layout, tags.Tags.Where(tag => repeated.Contains(tag.Key) || (!Regenerated(tags.Layout, tag.Key) && !RoundTrips(tags, tag))));
        return lost is null ? null : $"Trimming it without converting would lose or change some of its tags ({lost}), so it is left alone.";
    }

    private static string? Named(MusicTagLayout layout, IEnumerable<MusicTag> tags)
    {
        var names = tags.Select(tag => Describe(layout, tag)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return null;
        return string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" and {names.Count - 6} more" : "");
    }

    private static bool RoundTrips(MusicTagSet tags, MusicTag tag) => tags.Layout switch
    {
        // A genre number FFmpeg has no name for is written back as 255, "none".
        MusicTagLayout.Id3 when tag.Key.StartsWith("ID3V1:", StringComparison.Ordinal) =>
            tag.Key != "ID3V1:GENRE" || tag.Values is ["255"] || (tag.Values is [var number] && Id3v1.GenreName(number) is not null),
        MusicTagLayout.Id3 => tag.Key switch
        {
            "PRIV" => true,
            _ when tag.Key.StartsWith("APIC:", StringComparison.Ordinal) => PictureMimeTypes.Contains(MimeType(tag.Key)),
            _ when tag.Key.StartsWith("TXXX:", StringComparison.Ordinal) => tag.Values.Count == 1 && tag.Key.Length > 5,
            // FFmpeg reads a genre that starts with a number or a bracket as ID3v1 genre numbers, so
            // "(17)" and "80s Rock" both come back as other names.
            "TCON" => tag.Values is [var genre] && genre.TrimStart() is not [(>= '0' and <= '9') or '(', ..],
            _ => tag.Values.Count == 1 && (CommonTextFrames.Contains(tag.Key)
                || (tags.Id3Version == 3 ? Id3v23TextFrames : Id3v24TextFrames).Contains(tag.Key)),
        },
        // RIFF INFO entries come back as they were, except ITRK, which FFmpeg writes as IPRT.
        MusicTagLayout.Wav => tag.Key.StartsWith("INFO:", StringComparison.Ordinal) && tag.Key != "INFO:ITRK",
        // Vorbis comments: one value per field, under a name FFmpeg neither renames nor consumes.
        _ when tag.Key.StartsWith("PICTURE:", StringComparison.Ordinal) =>
            tags.Layout == MusicTagLayout.Flac && PictureMimeTypes.Contains(MimeType(tag.Key)),
        _ => tag.Values.Count == 1
            && !tag.Key.Contains(':', StringComparison.Ordinal)
            && tag.Key is not ("COMMENT" or "WAVEFORMATEXTENSIBLE_CHANNEL_MASK" or "METADATA_BLOCK_PICTURE" or "CUESHEET")
            && !tag.Key.StartsWith("CHAPTER", StringComparison.Ordinal),
    };

    // A picture's key is its kind, type, MIME type and description, in that order (see MusicTagReader).
    private static string MimeType(string pictureKey) => pictureKey.Split(':') is [_, _, var mime, ..] ? mime : "";

    private static string Describe(MusicTagLayout layout, MusicTag tag)
    {
        var name = Shorten(layout == MusicTagLayout.Id3 && tag.Key.StartsWith("TXXX:", StringComparison.Ordinal) ? tag.Key : tag.Key.Split(':')[0]);
        return tag.Values.Count > 1 && layout != MusicTagLayout.Id3 ? $"{name} ×{tag.Values.Count}" : name;
    }

    private static string Shorten(string name) => name.Length > MaximumNameLength ? name[..MaximumNameLength] + "…" : name;

    /// <summary>
    /// Kept only when every source tag is in the output with exactly the same values, as many times
    /// as the source has it, and the output has no tag the source lacks, apart from the few written
    /// again for the new file (FFmpeg's encoder name, Optimisarr's marker) and the ID3v2 frames FFmpeg
    /// writes from an ID3v1-only file's fields. An MP3 keeps its ID3v2 version and its ID3v1 tag.
    /// </summary>
    public static TagComparison Compare(MusicTagSet source, MusicTagSet output)
    {
        if (source.Layout != output.Layout)
            return new(false, $"The tags changed layout from {source.Layout} to {output.Layout}.");
        if (source.Layout == MusicTagLayout.Id3)
        {
            // A file without an ID3v2 tag is given FFmpeg's own; one it has keeps its version.
            if (source.Id3Version is { } version && output.Id3Version != version)
                return new(false, output.Id3Version is { } written
                    ? $"The ID3v2 tag changed from version 2.{version} to 2.{written}."
                    : "The ID3v2 tag was lost.");
            if (source.HasId3v1 != output.HasId3v1)
                return new(false, source.HasId3v1 ? "The ID3v1 tag was lost." : "An ID3v1 tag was added.");
        }

        var sourceByKey = Group(source);
        var outputByKey = Group(output);
        var lost = new List<string>();
        var changed = new List<string>();
        foreach (var (key, entries) in sourceByKey)
        {
            if (Regenerated(source.Layout, key)) continue;
            if (!outputByKey.TryGetValue(key, out var kept)) lost.Add(key);
            else if (!SameEntries(entries, kept)) changed.Add(key);
        }
        var mirrored = Id3v1Mirrored(source);
        var gained = outputByKey
            .Where(pair => !sourceByKey.ContainsKey(pair.Key) && !Regenerated(output.Layout, pair.Key)
                && !(mirrored.TryGetValue(pair.Key, out var expected) && pair.Value is [{ LegacyText: false, Values: [var value] }] && value == expected))
            .Select(pair => pair.Key)
            .ToList();

        if (lost.Count == 0 && changed.Count == 0 && gained.Count == 0)
            return new(true, $"All {sourceByKey.Keys.Count(key => !Regenerated(source.Layout, key))} source tag(s) are unchanged.");
        var parts = new List<string>();
        if (lost.Count > 0) parts.Add($"lost {string.Join(", ", lost.Take(8).Select(Shorten))}");
        if (changed.Count > 0) parts.Add($"changed {string.Join(", ", changed.Take(8).Select(Shorten))}");
        if (gained.Count > 0) parts.Add($"gained {string.Join(", ", gained.Take(8).Select(Shorten))}");
        return new(false, $"The tags {string.Join(" and ", parts)}.");
    }

    /// <summary>
    /// The ID3v2 frames FFmpeg 7.1 writes from an MP3 that has only an ID3v1 tag, with the values they
    /// must hold: each field as it is, the comment as TXXX:comment, and the genre by its name.
    /// </summary>
    private static Dictionary<string, string> Id3v1Mirrored(MusicTagSet source)
    {
        var mirrored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.Layout != MusicTagLayout.Id3 || source.Id3Version is not null || !source.HasId3v1) return mirrored;
        foreach (var tag in source.Tags)
        {
            if (tag.LegacyText || tag.Values is not [var value]) continue;
            var frame = tag.Key switch
            {
                "ID3V1:TITLE" => "TIT2",
                "ID3V1:ARTIST" => "TPE1",
                "ID3V1:ALBUM" => "TALB",
                "ID3V1:YEAR" => "TDRC",
                "ID3V1:COMMENT" => "TXXX:comment",
                "ID3V1:TRACK" => "TRCK",
                _ => null,
            };
            if (frame is not null) mirrored[frame] = value;
            else if (tag.Key == "ID3V1:GENRE" && Id3v1.GenreName(value) is { } genre) mirrored["TCON"] = genre;
        }
        return mirrored;
    }

    private static Dictionary<string, List<MusicTag>> Group(MusicTagSet tags) =>
        tags.Tags.GroupBy(tag => tag.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static bool SameEntries(List<MusicTag> source, List<MusicTag> output)
    {
        if (source.Count != output.Count) return false;
        var remaining = output.ToList();
        foreach (var entry in source)
        {
            var match = remaining.IndexOf(entry);
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return true;
    }
}
