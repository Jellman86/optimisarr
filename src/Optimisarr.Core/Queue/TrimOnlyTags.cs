using System.Text.RegularExpressions;
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
/// lost; a WAV file keeps only its LIST INFO entries. <see cref="Refusal"/> leaves such a file alone before any work; <see cref="Compare"/> is the
/// verification that refuses any loss it did not foresee.
/// </summary>
public static partial class TrimOnlyTags
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

    /// <summary>Why the file is left alone, or null when every tag it has is known to come back unchanged.</summary>
    public static string? Refusal(MusicTagRead read)
    {
        if (read.Tags is not { } tags)
        {
            var problem = read.Problem ?? "no detail";
            if (problem.Length > 200) problem = problem[..200] + "…";
            return $"Its tags could not be read in full ({problem}), so it is not trimmed without converting it.";
        }

        var lost = tags.Tags
            .Where(tag => !Regenerated(tags.Layout, tag.Key) && !RoundTrips(tags, tag)
                // FFmpeg keeps one value per key, so a second frame or entry of the same kind is lost.
                || tags.Layout is MusicTagLayout.Id3 or MusicTagLayout.Wav && tags.Tags.Count(other => other.Key == tag.Key) > 1)
            .Select(tag => Describe(tags.Layout, tag))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (lost.Count == 0) return null;
        var named = string.Join(", ", lost.Take(6)) + (lost.Count > 6 ? $" and {lost.Count - 6} more" : "");
        return $"Trimming it without converting would lose or change some of its tags ({named}), so it is left alone.";
    }

    private static bool RoundTrips(MusicTagSet tags, MusicTag tag) => tags.Layout switch
    {
        MusicTagLayout.Id3 when tag.Key.StartsWith("ID3V1:", StringComparison.Ordinal) => true,
        MusicTagLayout.Id3 => tag.Key switch
        {
            "PRIV" => true,
            _ when tag.Key.StartsWith("APIC:", StringComparison.Ordinal) => true,
            _ when tag.Key.StartsWith("TXXX:", StringComparison.Ordinal) => tag.Values.Count == 1 && tag.Key.Length > 5,
            "TCON" => tag.Values.Count == 1 && !NumericGenre().IsMatch(tag.Values[0]),
            _ => tag.Values.Count == 1 && (CommonTextFrames.Contains(tag.Key)
                || (tags.Id3Version == 3 ? Id3v23TextFrames : Id3v24TextFrames).Contains(tag.Key)),
        },
        // RIFF INFO entries come back as they were, except ITRK, which FFmpeg writes as IPRT.
        MusicTagLayout.Wav => tag.Key.StartsWith("INFO:", StringComparison.Ordinal) && tag.Key != "INFO:ITRK",
        // Vorbis comments: one value per field, under a name FFmpeg neither renames nor consumes.
        _ when tag.Key.StartsWith("PICTURE:", StringComparison.Ordinal) => tags.Layout == MusicTagLayout.Flac,
        _ => tag.Values.Count == 1
            && !tag.Key.Contains(':', StringComparison.Ordinal)
            && tag.Key is not ("COMMENT" or "WAVEFORMATEXTENSIBLE_CHANNEL_MASK" or "METADATA_BLOCK_PICTURE" or "CUESHEET")
            && !tag.Key.StartsWith("CHAPTER", StringComparison.Ordinal),
    };

    // "(17)", "17" or "(RX)": a genre by its ID3v1 number, which FFmpeg writes back as its name.
    [GeneratedRegex(@"^\s*(\(\w+\)|\d+\s*$)")]
    private static partial Regex NumericGenre();

    private static string Describe(MusicTagLayout layout, MusicTag tag)
    {
        var name = tag.Key.Split(':')[0];
        if (layout == MusicTagLayout.Id3 && name == "TXXX") name = tag.Key.Length > 40 ? tag.Key[..40] + "…" : tag.Key;
        return tag.Values.Count > 1 && layout != MusicTagLayout.Id3 ? $"{name} ×{tag.Values.Count}" : name;
    }

    /// <summary>
    /// Kept only when every source tag is in the output with exactly the same values, as many times
    /// as the source has it, apart from the few written again for the new file. Tags the output adds
    /// are allowed: FFmpeg adds its encoder name and Optimisarr its marker.
    /// </summary>
    public static TagComparison Compare(MusicTagSet source, MusicTagSet output)
    {
        if (source.Layout != output.Layout)
            return new(false, $"The tags changed layout from {source.Layout} to {output.Layout}.");

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

        if (lost.Count == 0 && changed.Count == 0)
            return new(true, $"All {sourceByKey.Keys.Count(key => !Regenerated(source.Layout, key))} source tag(s) are unchanged.");
        var parts = new List<string>();
        if (lost.Count > 0) parts.Add($"lost {string.Join(", ", lost.Take(8))}");
        if (changed.Count > 0) parts.Add($"changed {string.Join(", ", changed.Take(8))}");
        return new(false, $"The tags {string.Join(" and ", parts)}.");
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
