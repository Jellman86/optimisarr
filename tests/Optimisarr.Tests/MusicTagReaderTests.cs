using System.Text;
using Optimisarr.Core.Library;
using static Optimisarr.Tests.MusicTagFixtures;

namespace Optimisarr.Tests;

public sealed class MusicTagReaderTests
{
    private static MusicTagRead Read(string extension, byte[] file) => MusicTagReader.Read(extension, new MusicTagBytes(file));

    private static MusicTagSet Tags(string extension, byte[] file) =>
        Read(extension, file).Tags ?? throw new Xunit.Sdk.XunitException(Read(extension, file).Problem);

    [Fact]
    public void Id3_text_reads_the_same_whatever_encoding_and_version_wrote_it()
    {
        var v23 = Tags(".mp3", [.. Id3v2(3, Text(3, "TIT2", "Café"), Text(3, "TPE1", "Artist")), .. AudioFrames]);
        // FFmpeg ends each value with a NUL, which is not part of the text.
        var v24 = Tags(".mp3", [.. Id3v2(4, Frame("TIT2", [3, .. "Café\0"u8.ToArray()]), Latin1Frame("TPE1", "\0Artist")), .. AudioFrames]);

        Assert.Equal(3, v23.Id3Version);
        Assert.Equal(4, v24.Id3Version);
        Assert.Equal(v23.Tags, v24.Tags);
        Assert.Equal([new MusicTag("TIT2", ["Café"]), new MusicTag("TPE1", ["Artist"])], v23.Tags);
    }

    [Fact]
    public void An_id3v24_text_frame_keeps_each_of_its_values()
    {
        var tags = Tags(".mp3", [.. Id3v2(4, Text(4, "TPE1", "A", "B")), .. AudioFrames]);

        Assert.Equal(["A", "B"], tags.Tags.Single().Values);
    }

    [Fact]
    public void Described_frames_are_told_apart_by_their_description_and_language()
    {
        var tags = Tags(".mp3", [.. Id3v2(4,
            Frame("TXXX", [3, .. "MusicBrainz Album Id\0abc"u8.ToArray()]),
            Frame("COMM", [0, .. "eng"u8.ToArray(), .. "\0A comment"u8.ToArray()]),
            Frame("USLT", [3, .. "eng"u8.ToArray(), .. "\0la la"u8.ToArray()]),
            Frame("WXXX", [0, .. "\0http://example.org"u8.ToArray()]),
            Latin1Frame("WOAR", "http://artist.example")), .. AudioFrames]);

        Assert.Equal([
            new MusicTag("TXXX:MusicBrainz Album Id", ["abc"]), new MusicTag("COMM:eng:", ["A comment"]),
            new MusicTag("USLT:eng:", ["la la"]), new MusicTag("WXXX:", ["http://example.org"]),
            new MusicTag("WOAR", ["http://artist.example"])], tags.Tags);
    }

    [Fact]
    public void A_picture_is_known_by_its_type_and_description_and_compared_by_its_bytes()
    {
        byte[] Picture(byte last) => [0, .. "image/jpeg\0"u8.ToArray(), 3, .. "Front\0"u8.ToArray(), 0xFF, 0xD8, last];

        var one = Tags(".mp3", [.. Id3v2(3, Frame("APIC", Picture(1))), .. AudioFrames]).Tags.Single();
        var other = Tags(".mp3", [.. Id3v2(3, Frame("APIC", Picture(2))), .. AudioFrames]).Tags.Single();

        Assert.Equal("APIC:3:Front", one.Key);
        Assert.NotEqual(one, other);
    }

    [Fact]
    public void Binary_frames_are_compared_by_their_bytes()
    {
        var tags = Tags(".mp3", [.. Id3v2(4, Frame("POPM", [.. "x@y\0"u8.ToArray(), 196, 0, 0, 0, 5]), Frame("UFID", "http://musicbrainz.org\01234"u8.ToArray())), .. AudioFrames]);

        Assert.Equal(["POPM", "UFID"], tags.Tags.Select(tag => tag.Key));
        Assert.All(tags.Tags, tag => Assert.Matches("^[0-9a-f]{64}$", tag.Values.Single()));
    }

    [Fact]
    public void A_2_3_tag_with_unsynchronisation_and_an_extended_header_is_read()
    {
        // The whole 2.3 tag is unsynchronised: every 0xFF is followed by an inserted 0x00.
        byte[] extended = [0, 0, 0, 6, 0, 0, 0, 0, 0, 0];
        var plain = Id3v2(3, 0x40, 0, Frame("PRIV", [.. "owner\0"u8.ToArray(), 0xFF, 0xE0]));
        var body = plain[10..].ToList();
        body.InsertRange(0, extended);
        var unsynchronised = new List<byte>();
        foreach (var b in body)
        {
            unsynchronised.Add(b);
            if (b == 0xFF) unsynchronised.Add(0);
        }
        byte[] file = [.. "ID3"u8.ToArray(), 3, 0, 0xC0, .. Syncsafe(unsynchronised.Count), .. unsynchronised, .. AudioFrames];

        var expected = Tags(".mp3", [.. Id3v2(3, Frame("PRIV", [.. "owner\0"u8.ToArray(), 0xFF, 0xE0])), .. AudioFrames]);
        Assert.Equal(expected.Tags, Tags(".mp3", file).Tags);
    }

    [Fact]
    public void A_2_4_frame_with_its_own_unsynchronisation_and_data_length_is_read()
    {
        byte[] content = [.. "owner\0"u8.ToArray(), 0xFF, 0xE0];
        // Format flags 0x03: unsynchronised, with a four-byte data length before the data.
        var tag = Id3v2(4, Frame("PRIV", [.. Syncsafe(content.Length), .. "owner\0"u8.ToArray(), 0xFF, 0x00, 0xE0]));
        tag[10 + 9] = 0x03;

        var expected = Tags(".mp3", [.. Id3v2(4, Frame("PRIV", content)), .. AudioFrames]);
        Assert.Equal(expected.Tags, Tags(".mp3", [.. tag, .. AudioFrames]).Tags);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Other_id3_versions_cannot_be_proved(byte version)
    {
        var tag = Id3v2(3, Text(3, "TIT2", "T"));
        tag[3] = version;

        Assert.Null(Read(".mp3", [.. tag, .. AudioFrames]).Tags);
    }

    [Fact]
    public void Compressed_frames_truncated_frames_and_data_after_the_padding_cannot_be_proved()
    {
        var compressed = Id3v2(4, Text(4, "TIT2", "T"));
        compressed[10 + 9] = 0x08;
        var truncated = Id3v2(4, 0, 0, Text(4, "TIT2", "Title"));
        truncated[10 + 7] = 100;
        var trailing = Id3v2(4, 0, 4, Text(4, "TIT2", "T"));
        trailing[^1] = 7;

        Assert.Contains("compressed", Read(".mp3", [.. compressed, .. AudioFrames]).Problem);
        Assert.Contains("truncated", Read(".mp3", [.. truncated, .. AudioFrames]).Problem);
        Assert.Contains("padding", Read(".mp3", [.. trailing, .. AudioFrames]).Problem);
        Assert.Contains("cut short", Read(".mp3", Id3v2(4, Text(4, "TIT2", "T"))[..20]).Problem);
    }

    [Fact]
    public void An_id3v1_tag_is_read_and_an_ape_tag_cannot_be_proved()
    {
        var tags = Tags(".mp3", [.. Id3v2(3, Text(3, "TIT2", "T")), .. AudioFrames, .. Id3v1("Title", "Artist", 3, 17)]);
        byte[] apeFooter = [.. "APETAGEX"u8.ToArray(), .. new byte[24]];

        Assert.True(tags.HasId3v1);
        Assert.Contains(new MusicTag("ID3V1:TITLE", ["Title"]), tags.Tags);
        Assert.Contains(new MusicTag("ID3V1:TRACK", ["3"]), tags.Tags);
        Assert.Contains(new MusicTag("ID3V1:GENRE", ["17"]), tags.Tags);
        Assert.Equal("an APE tag", Read(".mp3", [.. AudioFrames, .. apeFooter, .. Id3v1("T", "A", 1, 0)]).Problem);
    }

    [Fact]
    public void An_mp3_without_tags_has_none()
    {
        var tags = Tags(".mp3", AudioFrames);

        Assert.Empty(tags.Tags);
        Assert.Null(tags.Id3Version);
        Assert.False(tags.HasId3v1);
    }

    [Fact]
    public void A_flac_field_keeps_every_value_in_order_and_names_compare_without_case()
    {
        var tags = Tags(".flac", Flac(
            FlacBlock(0, new byte[34]),
            FlacBlock(3, new byte[18]),
            FlacBlock(4, VorbisComment("reference libFLAC", ("ARTIST", "A"), ("title", "T"), ("Artist", "B"))),
            FlacBlock(1, new byte[100], last: true)));

        Assert.Equal([new MusicTag("ARTIST", ["A", "B"]), new MusicTag("TITLE", ["T"])], tags.Tags);
    }

    [Fact]
    public void Flac_application_cue_sheet_and_picture_blocks_are_part_of_its_tags()
    {
        var tags = Tags(".flac", Flac(
            FlacBlock(0, new byte[34]),
            FlacBlock(2, [.. "TEST"u8.ToArray(), 1, 2]),
            FlacBlock(5, new byte[20]),
            FlacBlock(6, FlacPicture(3, "Front", [0xFF, 0xD8, 1]), last: true)));

        Assert.Equal(["APPLICATION:54455354", "CUESHEET", "PICTURE:3:Front"], tags.Tags.Select(tag => tag.Key));
    }

    [Fact]
    public void A_flac_file_that_does_not_start_as_one_cannot_be_proved()
    {
        Assert.Contains("ID3", Read(".flac", [.. Id3v2(4, Text(4, "TIT2", "T")), .. Flac(FlacBlock(0, new byte[34], last: true))]).Problem);
        Assert.Contains("cut short", Read(".flac", Flac(FlacBlock(0, new byte[34]))[..30]).Problem);
        Assert.Contains("comment", Read(".flac", Flac(FlacBlock(4, [1, 2, 3], last: true))).Problem);
    }

    [Fact]
    public void An_opus_comment_header_is_read_even_when_it_spans_pages()
    {
        var tags = OpusTags(("TITLE", "T"), ("ARTIST", "A"), ("ARTIST", "B"), ("R128_TRACK_GAIN", "-512"), ("PADDING", new string('x', 600)));
        // The header packet continues across a page boundary at a 255-byte lacing value.
        var split = 510;
        var first = OggPage(7, 0, OpusHead);
        var secondHeader = OggPage(7, 1, tags[..split]);
        // A page whose packet continues ends with a 255 lacing value and no terminator.
        secondHeader = [.. secondHeader[..26], 2, 255, 255, .. tags[..split]];
        byte[] file = [.. first, .. secondHeader, .. OggPage(7, 2, tags[split..]), .. OggPage(7, 3, [1, 2, 3])];

        var read = Tags(".opus", file);

        Assert.Equal(MusicTagLayout.OggOpus, read.Layout);
        Assert.Contains(new MusicTag("ARTIST", ["A", "B"]), read.Tags);
        Assert.Contains(new MusicTag("R128_TRACK_GAIN", ["-512"]), read.Tags);
    }

    [Fact]
    public void An_ogg_file_that_is_not_plain_opus_cannot_be_proved()
    {
        byte[] vorbis = [.. OggPage(7, 0, [1, .. "vorbis"u8.ToArray()]), .. OggPage(7, 1, [3, .. "vorbis"u8.ToArray()])];
        byte[] twoStreams = [.. OggPage(7, 0, OpusHead), .. OggPage(8, 0, OpusHead)];

        Assert.Contains("not an Opus stream", Read(".ogg", vorbis).Problem);
        Assert.Contains("more than one", Read(".opus", twoStreams).Problem);
    }

    [Fact]
    public void A_wav_files_info_entries_are_its_tags_and_any_other_chunk_is_kept_whole()
    {
        var tags = Tags(".wav", Wav(
            InfoList(("INAM", "Title"), ("IART", "Artist")),
            RiffChunk("bext", new byte[602]),
            RiffChunk("data", new byte[4000])));

        Assert.Equal(["INFO:INAM", "INFO:IART", "CHUNK:bext"], tags.Tags.Select(tag => tag.Key));
        Assert.Equal(["Title"], tags.Tags[0].Values);
    }

    [Fact]
    public void A_wav_chunk_running_past_the_end_cannot_be_proved()
    {
        var wav = Wav(RiffChunk("data", new byte[4000]));

        Assert.Contains("truncated", Read(".wav", wav[..2000]).Problem);
        Assert.Contains("RIFF", Read(".wav", Encoding.ASCII.GetBytes("RF64....WAVE")).Problem);
    }
}
