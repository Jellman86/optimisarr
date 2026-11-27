using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using static Optimisarr.Tests.Mp4TagFixtures;

namespace Optimisarr.Tests;

public sealed class Mp4TagCarryOverTests
{
    private static Mp4Tags Tags(byte[] file) =>
        Mp4TagReader.Read(new MusicTagBytes(file)).Tags ?? throw new Xunit.Sdk.XunitException(Mp4TagReader.Read(new MusicTagBytes(file)).Problem);

    private static List<Mp4TagItem> Planned(IReadOnlyList<ReadOnlyMemory<byte>> items) =>
        [.. Tags(M4a([.. items.Select(item => item.ToArray())])).Items];

    private static Mp4TagItem Item(byte[] atom) => Tags(M4a(atom)).Items.Single();

    private static readonly Mp4TagItem Marker = Item(Freeform("com.optimisarr", "optimisarr", "1.2.3"));

    // What FFmpeg 7.1 writes for a FLAC file's tags in an M4A: the tags it has an atom for, without totals.
    private static readonly byte[] FfmpegOutput = M4a(Text("©nam", "Title"), Text("©too", "Lavf61.7.103"),
        Box("trkn", DataBox(0, [0, 0, 0, 3, 0, 0, 0, 0])), Box("disk", DataBox(0, [0, 0, 0, 1, 0, 0])));

    // As the probe reports them: the container's tags, then the audio stream's, which every M4A
    // FFmpeg writes has, with no language of its own.
    private static readonly Dictionary<string, string> FfmpegOutputTags = new()
    {
        ["major_brand"] = "M4A ", ["minor_version"] = "512", ["compatible_brands"] = "M4A isomiso2",
        ["title"] = "Title", ["encoder"] = "Lavf61.7.103", ["track"] = "3", ["disc"] = "1",
        ["language"] = "und", ["handler_name"] = "SoundHandler", ["vendor_id"] = "[0][0][0][0]",
    };

    private static readonly Dictionary<string, string> StreamTagsOnly = new()
    {
        ["major_brand"] = "M4A ", ["title"] = "Title", ["encoder"] = "Lavf61.7.103",
        ["language"] = "und", ["handler_name"] = "SoundHandler", ["vendor_id"] = "[0][0][0][0]",
    };

    private static readonly byte[] FfmpegOutputWithoutNumbers = M4a(Text("©nam", "Title"), Text("©too", "Lavf61.7.103"));

    [Fact]
    public void Tags_from_another_format_that_ffmpeg_has_no_atom_for_become_freeform_items()
    {
        var source = new Dictionary<string, string>
        {
            ["TITLE"] = "Title", ["track"] = "3", ["disc"] = "1", ["MUSICBRAINZ_ALBUMID"] = "abc", ["REPLAYGAIN_TRACK_GAIN"] = "-6.5 dB",
            ["ALBUMARTISTSORT"] = "Sort", ["LANGUAGE"] = "eng", ["encoder"] = "Lavf61.7.103",
        };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), null, source, FfmpegOutputTags, "eng", "1.2.3"));

        Assert.Equal([
            Item(Text("©nam", "Title")), Item(Text("©too", "Lavf61.7.103")),
            Item(Box("trkn", DataBox(0, [0, 0, 0, 3, 0, 0, 0, 0]))), Item(Box("disk", DataBox(0, [0, 0, 0, 1, 0, 0]))),
            Item(Freeform("com.apple.iTunes", "LANGUAGE", "eng")),
            Item(Freeform("com.apple.iTunes", "MusicBrainz Album Id", "abc")),
            Item(Freeform("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN", "-6.5 dB")),
            Item(Text("soaa", "Sort")),
            Marker,
        ], items);
    }

    [Fact]
    public void Track_and_disc_totals_are_written_into_the_track_and_disc_numbers()
    {
        var source = new Dictionary<string, string> { ["track"] = "3", ["TRACKTOTAL"] = "12", ["disc"] = "1", ["DISCTOTAL"] = "2" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), null, source, FfmpegOutputTags, null, "1.2.3"));

        Assert.Contains(Item(Box("trkn", DataBox(0, [0, 0, 0, 3, 0, 12, 0, 0]))), items);
        Assert.Contains(Item(Box("disk", DataBox(0, [0, 0, 0, 1, 0, 2]))), items);
        Assert.Single(items, item => item.Key == "trkn");
        Assert.DoesNotContain(items, item => item.Key.Contains("TOTAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tag_ffmpeg_wrote_with_another_value_is_left_for_verification_to_refuse()
    {
        var source = new Dictionary<string, string> { ["TITLE"] = "Source title", ["track"] = "3" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), null, source, FfmpegOutputTags, null, "1.2.3"));

        Assert.Contains(Item(Text("©nam", "Title")), items);
        Assert.Contains(Item(Box("trkn", DataBox(0, [0, 0, 0, 3, 0, 0, 0, 0]))), items);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public void Only_a_language_the_source_s_tags_state_is_carried_never_its_track_s_language()
    {
        // A Matroska or MP4 source's audio track says "und"; the probe merges that into its tags.
        var source = new Dictionary<string, string> { ["TITLE"] = "Title", ["language"] = "und" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), null, source, FfmpegOutputTags, null, "1.2.3"));

        Assert.DoesNotContain(items, item => item.Key.EndsWith(":LANGUAGE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_language_item_already_in_the_new_file_is_not_written_again()
    {
        var output = Tags(M4a(Text("©nam", "Title"), Freeform("com.apple.iTunes", "LANGUAGE", "eng")));

        var items = Planned(Mp4TagCarryOver.Plan(output, null, new Dictionary<string, string> { ["TITLE"] = "Title" },
            StreamTagsOnly, "eng", "1.2.3"));

        Assert.Single(items, item => item.Key == Mp4TagCarryOver.LanguageKey);
    }

    [Fact]
    public void A_track_or_disc_number_the_number_atoms_cannot_hold_is_kept_as_text()
    {
        // FFmpeg writes no number atom for a number it reads as zero.
        var source = new Dictionary<string, string> { ["TITLE"] = "Title", ["track"] = "A1", ["disc"] = "0" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutputWithoutNumbers), null, source, StreamTagsOnly, null, "1.2.3"));

        Assert.Contains(Item(Freeform("com.apple.iTunes", "track", "A1")), items);
        Assert.Contains(Item(Freeform("com.apple.iTunes", "disc", "0")), items);
        Assert.DoesNotContain(items, item => item.Key is "trkn" or "disk");
    }

    [Fact]
    public void A_number_ffmpeg_read_only_the_start_of_is_kept_as_the_source_s_text()
    {
        // FFmpeg reads "3abc" as track 3.
        var source = new Dictionary<string, string> { ["TITLE"] = "Title", ["track"] = "3abc", ["disc"] = "1" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), null, source, FfmpegOutputTags, null, "1.2.3"));

        Assert.Contains(Item(Freeform("com.apple.iTunes", "track", "3abc")), items);
        Assert.DoesNotContain(items, item => item.Key == "trkn");
        Assert.Contains(items, item => item.Key == "disk");
    }

    [Fact]
    public void A_tag_whose_name_holds_a_colon_is_kept_under_that_name()
    {
        var source = new Dictionary<string, string> { ["TITLE"] = "Title", ["FOO:BAR"] = "baz" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutputWithoutNumbers), null, source, StreamTagsOnly, null, "1.2.3"));

        Assert.Contains(Item(Freeform("com.apple.iTunes", "FOO:BAR", "baz")), items);
    }

    [Fact]
    public void An_m4a_source_keeps_its_own_items_exactly_apart_from_those_written_for_the_new_file()
    {
        var gnre = Box("gnre", DataBox(0, [0, 18]));
        var covr = Box("covr", DataBox(14, Png));
        var source = Tags(M4a(Text("©nam", "Title"), gnre, Text("©too", "iTunes 12.13"),
            Freeform("com.apple.iTunes", "iTunSMPB", " 00000000 00000840"), Freeform("com.apple.iTunes", "Encoding Params", "vers"),
            Freeform("com.apple.iTunes", "iTunNORM", " 00000100"), covr, Freeform("com.optimisarr", "optimisarr", "0.1.0")));
        // FFmpeg turned the numbered genre into text and wrote its own encoder name.
        var output = Tags(M4a(Text("©nam", "Title"), Text("©gen", "Pop"), Text("©too", "Lavf61.7.103")));

        var items = Planned(Mp4TagCarryOver.Plan(output, source, null, null, null, "1.2.3"));

        Assert.Equal([
            Item(Text("©nam", "Title")), Item(gnre), Item(Freeform("com.apple.iTunes", "iTunNORM", " 00000100")), Item(covr),
            Item(Text("©too", "Lavf61.7.103")), Marker,
        ], items);
    }

    [Fact]
    public void A_source_in_quicktime_keys_is_carried_over_from_its_tags_as_ffprobe_reads_them()
    {
        var source = Tags([.. Ftyp, .. Mdat, .. Moov(QuickTimeKeysMeta(("TITLE", "Title"), ("MUSICBRAINZ_ALBUMID", "abc")))]);
        var sourceTags = new Dictionary<string, string> { ["TITLE"] = "Title", ["MUSICBRAINZ_ALBUMID"] = "abc" };

        var items = Planned(Mp4TagCarryOver.Plan(Tags(FfmpegOutput), source, sourceTags, FfmpegOutputTags, null, "1.2.3"));

        Assert.Contains(Item(Freeform("com.apple.iTunes", "MusicBrainz Album Id", "abc")), items);
    }

    [Fact]
    public void An_earlier_marker_is_replaced()
    {
        var output = Tags(M4a(Text("©nam", "Title"), Freeform("com.optimisarr", "optimisarr", "0.1.0")));

        var items = Planned(Mp4TagCarryOver.Plan(output, null, new Dictionary<string, string>(), new Dictionary<string, string>(), null, "1.2.3"));

        Assert.Equal([Item(Text("©nam", "Title")), Marker], items);
    }

    [Theory]
    [InlineData("©too", true)]
    [InlineData("----:com.apple.iTunes:iTunSMPB", true)]
    [InlineData("----:com.apple.iTunes:Encoding Params", true)]
    [InlineData("----:com.optimisarr:optimisarr", true)]
    [InlineData("----:com.apple.iTunes:iTunNORM", false)]
    [InlineData("©nam", false)]
    [InlineData("covr", false)]
    public void Knows_which_items_are_written_again_for_a_new_file(string key, bool regenerated) =>
        Assert.Equal(regenerated, Mp4TagCarryOver.Regenerated(key));
}
