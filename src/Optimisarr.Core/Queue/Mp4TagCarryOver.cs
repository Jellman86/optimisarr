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

    /// <summary>The item Picard and Mp3tag write a music file's language tag as.</summary>
    public const string LanguageKey = $"----:{ItunesMean}:LANGUAGE";

    /// <summary>True when <paramref name="item"/> holds exactly <paramref name="value"/> as UTF-8 text, as this writes it.</summary>
    public static bool HoldsText(Mp4TagItem item, string value) =>
        item.Values is [var only] && only == $"1:{MusicTagReader.Sha256(System.Text.Encoding.UTF8.GetBytes(value))}";

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
    /// <param name="sourceLanguage">
    /// The language the source's own tags state (<see cref="MediaProbeResult.TagLanguage"/>). The
    /// probe's tags cannot say it: they hold every M4A track's "und" too.
    /// </param>
    public static IReadOnlyList<ReadOnlyMemory<byte>> Plan(
        Mp4Tags output,
        Mp4Tags? source,
        IReadOnlyDictionary<string, string>? sourceTags,
        IReadOnlyDictionary<string, string>? outputTags,
        string? sourceLanguage,
        string marker)
    {
        var items = source is { Layout: Mp4TagLayout.Itunes }
            ? [.. source.Items.Where(item => !Regenerated(item.Key)).Select(item => item.Atom),
                .. output.Items.Where(item => item.Key == "©too").Select(item => item.Atom)]
            : CarryOver(output, sourceTags, outputTags, sourceLanguage);
        items.Add(Mp4TagWriter.Freeform(MarkerMean, OptimisationMarker.MetadataKey, marker));
        return items;
    }

    private static List<ReadOnlyMemory<byte>> CarryOver(
        Mp4Tags output, IReadOnlyDictionary<string, string>? sourceTags, IReadOnlyDictionary<string, string>? outputTags, string? sourceLanguage)
    {
        var source = AudioFormatTags.Normalise(sourceTags);
        var written = AudioFormatTags.Normalise(outputTags);
        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal) { MarkerKey };
        var added = new List<(string Key, byte[] Atom)>();
        foreach (var (name, tag) in source)
        {
            if (name is "track" or "disc")
            {
                var atom = name == "track" ? "trkn" : "disk";
                if (!FitsNumberAtom(tag.Value))
                {
                    // FFmpeg writes the number it reads from the start of the text, or none for zero;
                    // either way the text itself is kept instead.
                    dropped.Add(atom);
                    added.Add(FreeformItem(tag.Name, tag.Value));
                }
                else if (written.TryGetValue(name, out var number) && WithTotal(tag.Value, number.Value) is { } pair)
                {
                    replaced[atom] = Mp4TagWriter.NumberPair(atom, pair.Number, pair.Total);
                }
                continue;
            }
            if (written.ContainsKey(name)) continue;
            if (AudioFormatTags.Mp4SortAtom(name) is { } sortAtom)
                added.Add((sortAtom, Mp4TagWriter.Text(sortAtom, tag.Value)));
            else
                added.Add(FreeformItem(AudioFormatTags.Mp4FreeformName(name) ?? tag.Name, tag.Value));
        }
        if (!string.IsNullOrWhiteSpace(sourceLanguage) && output.Items.All(item => item.Key != LanguageKey))
            added.Add((LanguageKey, Mp4TagWriter.Freeform(ItunesMean, "LANGUAGE", sourceLanguage.Trim())));
        return
        [
            .. output.Items.Where(item => !dropped.Contains(item.Key))
                .Select(item => replaced.TryGetValue(item.Key, out var atom) ? (ReadOnlyMemory<byte>)atom : item.Atom),
            .. added.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (ReadOnlyMemory<byte>)item.Atom),
        ];
    }

    private static (string Key, byte[] Atom) FreeformItem(string name, string value) =>
        ($"----:{ItunesMean}:{name}", Mp4TagWriter.Freeform(ItunesMean, name, value));

    /// <summary>A number, and total if it has one, that a <c>trkn</c> or <c>disk</c> atom holds as the same text.</summary>
    private static bool FitsNumberAtom(string value) =>
        value.Split('/') is [var number, .. var total] && total.Length <= 1
        && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)
        && position is > 0 and <= ushort.MaxValue
        && (total is [] || int.TryParse(total[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            && count is > 0 and <= ushort.MaxValue);

    /// <summary>The source's number and total when FFmpeg wrote the same number without the total.</summary>
    private static (int Number, int Total)? WithTotal(string source, string written) =>
        source.Split('/') is [var number, var total] && number == written
        && int.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var position) && int.TryParse(total, System.Globalization.CultureInfo.InvariantCulture, out var count) && position <= ushort.MaxValue && count is > 0 and <= ushort.MaxValue
            ? (position, count)
            : null;
}
