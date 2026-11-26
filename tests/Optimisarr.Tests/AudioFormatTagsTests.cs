using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class AudioFormatTagsTests
{
    private static IReadOnlyDictionary<string, string> Values(Dictionary<string, string> tags) =>
        AudioFormatTags.Normalise(tags).ToDictionary(pair => pair.Key, pair => pair.Value.Value);

    [Fact]
    public void The_same_tag_has_one_name_whichever_format_spelled_it()
    {
        var flac = Values(new() { ["MUSICBRAINZ_ALBUMID"] = "a", ["ALBUMARTISTSORT"] = "s", ["TITLESORT"] = "t", ["BPM"] = "120" });
        var mp3 = Values(new() { ["MusicBrainz Album Id"] = "a", ["TSO2"] = "s", ["title-sort"] = "t", ["TBPM"] = "120" });
        var m4a = Values(new() { ["MusicBrainz Album Id"] = "a", ["sort_album_artist"] = "s", ["sort_name"] = "t", ["BPM"] = "120" });

        Assert.Equal(flac, mp3);
        Assert.Equal(flac, m4a);
    }

    [Fact]
    public void A_track_total_belongs_to_the_track_number_and_numbers_compare_as_numbers()
    {
        var flac = Values(new() { ["track"] = "03", ["TRACKTOTAL"] = "12", ["disc"] = "1", ["TOTALDISCS"] = "2" });
        var m4a = Values(new() { ["track"] = "3/12", ["disc"] = "1/2" });

        Assert.Equal(m4a, flac);
        Assert.Equal("3/12", flac["track"]);
    }

    [Fact]
    public void A_track_number_that_is_not_a_number_is_kept_as_written()
    {
        Assert.Equal("A1", Values(new() { ["track"] = "A1", ["TRACKTOTAL"] = "12" })["track"]);
        Assert.Equal("12", Values(new() { ["track"] = "A1", ["TRACKTOTAL"] = "12" })["tracktotal"]);
    }

    [Fact]
    public void Technical_container_fields_and_empty_values_are_not_music_tags()
    {
        var tags = Values(new()
        {
            ["encoder"] = "Lavf", ["major_brand"] = "M4A ", ["iTunSMPB"] = " 0", ["Encoding Params"] = "vers", ["optimisarr"] = "1",
            ["TLEN"] = "1000", ["comment"] = "  ",
        });

        Assert.Empty(tags);
    }

    [Fact]
    public void Two_spellings_of_one_tag_with_different_values_are_both_kept()
    {
        var tags = Values(new() { ["TSO2"] = "First", ["ALBUMARTISTSORT"] = "Second" });

        Assert.Equal(2, tags.Count);
        Assert.Contains("First", tags.Values);
        Assert.Contains("Second", tags.Values);
    }

    [Fact]
    public void Each_tag_remembers_the_name_its_file_gave_it()
    {
        var tag = AudioFormatTags.Normalise(new Dictionary<string, string> { ["ACOUSTID_ID"] = "x" }).Single();

        Assert.Equal("acoustid id", tag.Key);
        Assert.Equal("ACOUSTID_ID", tag.Value.Name);
        Assert.Equal("Acoustid Id", AudioFormatTags.Mp4FreeformName(tag.Key));
    }
}
