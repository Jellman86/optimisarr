using Optimisarr.Core.Library;

namespace Optimisarr.Core.Queue;

/// <summary>
/// The iTunes items a converted M4A keeps. FFmpeg 7.1 writes iTunes atoms only for the tags it has
/// one for (title, artist, album, track and disc without their totals, and a few more) and drops
/// every other tag, Optimisarr's marker included; its <c>use_metadata_tags</c> flag keeps them all,
/// but as QuickTime keys that most music software cannot read, and without the cover art. So FFmpeg
/// writes what it can and the rest is added to its tag list afterwards, measured in #593.
/// </summary>
public static class Mp4TagCarryOver
{
    /// <summary>Who defined Optimisarr's freeform marker item. ffprobe reads it back as the plain marker tag.</summary>
    public const string MarkerMean = "com.optimisarr";

    private const string ItunesMean = "com.apple.iTunes";

    private static readonly string MarkerKey = $"----:{MarkerMean}:{OptimisationMarker.MetadataKey}";

    /// <summary>
    /// Written again for the new file, so the source's are not kept: the encoder name (<c>©too</c>),
    /// iTunes' gapless figures (<c>iTunSMPB</c>) and encoder settings (<c>Encoding Params</c>,
    /// <c>cdec</c>), all true only of the old encoding, and Optimisarr's marker.
    /// </summary>
    public static bool Regenerated(string key) =>
        key == "©too" || key == MarkerKey
        || key.StartsWith($"----:{ItunesMean}:", StringComparison.Ordinal)
            && key[(ItunesMean.Length + 6)..] is var name
            && (name.Equals("iTunSMPB", StringComparison.OrdinalIgnoreCase) || name.Equals("Encoding Params", StringComparison.OrdinalIgnoreCase)
                || name.Equals("cdec", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The items for the new file's tag list. An M4A source with iTunes tags keeps its own items
    /// exactly, apart from those <see cref="Regenerated"/>; any other source keeps what FFmpeg wrote,
    /// with each tag it dropped added as the atom music software looks for, or else as a freeform
    /// item under the tag's own name. A tag FFmpeg wrote with another value is left as it is, for
    /// verification to refuse. Optimisarr's marker is added either way.
    /// </summary>
    /// <param name="output">The tags FFmpeg wrote.</param>
    /// <param name="source">The source's MP4 tags, or null when it is not an MP4 file.</param>
    /// <param name="sourceTags">The source's tags as ffprobe reports them.</param>
    /// <param name="outputTags">The tags FFmpeg wrote, as ffprobe reports them.</param>
    public static IReadOnlyList<ReadOnlyMemory<byte>> Plan(
        Mp4Tags output,
        Mp4Tags? source,
        IReadOnlyDictionary<string, string>? sourceTags,
        IReadOnlyDictionary<string, string>? outputTags,
        string marker)
    {
        var items = source is { Layout: Mp4TagLayout.Itunes }
            ? [.. source.Items.Where(item => !Regenerated(item.Key)).Select(item => item.Atom),
                .. output.Items.Where(item => item.Key == "©too").Select(item => item.Atom)]
            : CarryOver(output, sourceTags, outputTags);
        items.Add(Mp4TagWriter.Freeform(MarkerMean, OptimisationMarker.MetadataKey, marker));
        return items;
    }

    private static List<ReadOnlyMemory<byte>> CarryOver(
        Mp4Tags output, IReadOnlyDictionary<string, string>? sourceTags, IReadOnlyDictionary<string, string>? outputTags)
    {
        var source = AudioFormatTags.Normalise(sourceTags, includeLanguage: true);
        var written = AudioFormatTags.Normalise(outputTags, includeLanguage: true);
        var items = output.Items.Where(item => item.Key != MarkerKey).ToList();
        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var added = new List<(string Key, byte[] Atom)>();
        foreach (var (name, tag) in source)
        {
            if (written.TryGetValue(name, out var kept))
            {
                if (name is "track" or "disc" && WithTotal(tag.Value, kept.Value) is { } pair)
                    replaced[name == "track" ? "trkn" : "disk"] = Mp4TagWriter.NumberPair(name == "track" ? "trkn" : "disk", pair.Number, pair.Total);
                continue;
            }
            if (name is "track" or "disc") continue;
            if (AudioFormatTags.Mp4SortAtom(name) is { } atom)
                added.Add((atom, Mp4TagWriter.Text(atom, tag.Value)));
            else
            {
                var freeformName = AudioFormatTags.Mp4FreeformName(name) ?? tag.Name;
                added.Add(($"----:{ItunesMean}:{freeformName}", Mp4TagWriter.Freeform(ItunesMean, freeformName, tag.Value)));
            }
        }
        return
        [
            .. items.Select(item => replaced.TryGetValue(item.Key, out var atom) ? (ReadOnlyMemory<byte>)atom : item.Atom),
            .. added.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (ReadOnlyMemory<byte>)item.Atom),
        ];
    }

    /// <summary>The source's number and total when FFmpeg wrote the same number without the total.</summary>
    private static (int Number, int Total)? WithTotal(string source, string written) =>
        source.Split('/') is [var number, var total] && number == written
        && int.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var position) && int.TryParse(total, System.Globalization.CultureInfo.InvariantCulture, out var count) && position <= ushort.MaxValue && count is > 0 and <= ushort.MaxValue
            ? (position, count)
            : null;
}
