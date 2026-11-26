using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using static Optimisarr.Tests.MusicTagFixtures;

namespace Optimisarr.Tests;

/// <summary>
/// The tags a trim must keep. The frames and fields here are the ones FFmpeg 7.1 was measured to
/// lose or rewrite when it copies an MP3, FLAC or Opus file (see TrimOnlyTags).
/// </summary>
public sealed class TrimOnlyTagsTests
{
    private static MusicTagRead Mp3(int version, params (string Id, byte[] Payload)[] frames) =>
        MusicTagReader.Read(".mp3", new MusicTagBytes([.. Id3v2(version, frames), .. AudioFrames]));

    private static MusicTagRead FlacWith(params (string Field, string Value)[] comments) =>
        MusicTagReader.Read(".flac", new MusicTagBytes(Flac(FlacBlock(0, new byte[34]), FlacBlock(4, VorbisComment("Lavf", comments), last: true))));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Ordinary_text_frames_and_cover_art_are_known_to_come_back_unchanged(int version)
    {
        var read = Mp3(version, Text(version, "TIT2", "Title"), Text(version, "TPE1", "Artist"), Text(version, "TALB", "Album"),
            Text(version, "TRCK", "3/12"), Text(version, version == 3 ? "TYER" : "TDRC", "2001"), Text(version, "TCON", "Rock"),
            Frame("TXXX", [3, .. "MusicBrainz Album Id\0abc"u8.ToArray()]),
            Frame("APIC", [0, .. "image/jpeg\0"u8.ToArray(), 3, 0, 0xFF, 0xD8]),
            Frame("PRIV", "owner\0x"u8.ToArray()),
            // Written again for the new file on purpose.
            Text(version, "TLEN", "183000"), Text(version, "TSSE", "LAME"), Frame("TXXX", [3, .. "iTunSMPB\0 0 1"u8.ToArray()]));

        Assert.Null(TrimOnlyTags.Refusal(read));
    }

    [Fact]
    public void An_mp3_with_both_an_id3v2_and_an_id3v1_tag_is_left_alone()
    {
        var both = MusicTagReader.Read(".mp3", new MusicTagBytes([.. Id3v2(3, Text(3, "TIT2", "Title")), .. AudioFrames, .. Id3v1("Title", "Artist", 1, 17)]));
        var v2Only = MusicTagReader.Read(".mp3", new MusicTagBytes([.. Id3v2(3, Text(3, "TIT2", "Title")), .. AudioFrames]));

        Assert.Equal("It has both an ID3v2 and an ID3v1 tag, and trimming without converting would rewrite the ID3v1 tag, so it is left alone.", TrimOnlyTags.Refusal(both));
        Assert.Null(TrimOnlyTags.Refusal(v2Only));
    }

    [Fact]
    public void Mp3_frames_ffmpeg_loses_or_rewrites_leave_the_file_alone_and_are_named()
    {
        var read = Mp3(4, Text(4, "TIT2", "Title"),
            Frame("POPM", [.. "x@y\0"u8.ToArray(), 196, 0, 0, 0, 5]),
            Frame("UFID", "http://musicbrainz.org\01234"u8.ToArray()),
            Frame("USLT", [3, .. "eng\0la la"u8.ToArray()]),
            Frame("COMM", [0, .. "eng\0A comment"u8.ToArray()]),
            Frame("PCNT", [0, 0, 0, 7]),
            Frame("SYLT", [0, .. "eng"u8.ToArray(), 2, 1, 0]),
            Latin1Frame("WOAR", "http://artist.example"));

        var refusal = TrimOnlyTags.Refusal(read);

        Assert.Equal("Trimming it without converting would lose or change some of its tags (POPM, UFID, USLT, COMM, PCNT, SYLT and 1 more), so it is left alone.", refusal);
    }

    [Fact]
    public void Mp3_values_ffmpeg_rewrites_leave_the_file_alone()
    {
        // A numeric genre comes back as its name; a 2.3 TCMP or TSOP as TXXX; a second value is joined.
        Assert.Contains("TCON", TrimOnlyTags.Refusal(Mp3(3, Text(3, "TCON", "(17)"))));
        Assert.Contains("TCMP", TrimOnlyTags.Refusal(Mp3(3, Text(3, "TCMP", "1"))));
        Assert.Contains("TSOP", TrimOnlyTags.Refusal(Mp3(3, Text(3, "TSOP", "Sort"))));
        Assert.Contains("TPE1", TrimOnlyTags.Refusal(Mp3(4, Text(4, "TPE1", "A", "B"))));
        Assert.Contains("TDRL", TrimOnlyTags.Refusal(Mp3(4, Text(4, "TDRL", "2001"))));
        Assert.Contains("TIT2", TrimOnlyTags.Refusal(Mp3(4, Text(4, "TIT2", "A"), Text(4, "TIT2", "B"))));
        Assert.Null(TrimOnlyTags.Refusal(Mp3(4, Text(4, "TSOP", "Sort"))));
        // FFmpeg's keys ignore case, so a second description differing only in case is lost.
        Assert.Contains("TXXX:foo", TrimOnlyTags.Refusal(Mp3(4, Frame("TXXX", [3, .. "Foo\0a"u8.ToArray()]), Frame("TXXX", [3, .. "foo\0b"u8.ToArray()]))));
    }

    [Fact]
    public void Flac_fields_ffmpeg_joins_renames_or_drops_leave_the_file_alone()
    {
        Assert.Contains("ARTIST ×2", TrimOnlyTags.Refusal(FlacWith(("ARTIST", "A"), ("ARTIST", "B"))));
        Assert.Contains("COMMENT", TrimOnlyTags.Refusal(FlacWith(("COMMENT", "c"))));
        Assert.Contains("CHAPTER001", TrimOnlyTags.Refusal(FlacWith(("CHAPTER001", "00:00:01.000"))));
        Assert.Null(TrimOnlyTags.Refusal(FlacWith(("TITLE", "T"), ("ARTIST", "A"), ("DESCRIPTION", "d"), ("REPLAYGAIN_TRACK_GAIN", "-3 dB"), ("ENCODER", "x"))));

        var application = MusicTagReader.Read(".flac", new MusicTagBytes(Flac(FlacBlock(0, new byte[34]), FlacBlock(2, [.. "TEST"u8.ToArray(), 1], last: true))));
        Assert.Contains("APPLICATION", TrimOnlyTags.Refusal(application));
    }

    [Fact]
    public void Wav_info_entries_are_kept_but_other_chunks_and_itrk_are_not()
    {
        MusicTagRead Wave(params byte[][] chunks) => MusicTagReader.Read(".wav", new MusicTagBytes(Wav(chunks)));

        Assert.Null(TrimOnlyTags.Refusal(Wave(InfoList(("INAM", "T"), ("IART", "A"), ("ISFT", "Lavf")), RiffChunk("data", new byte[8]))));
        Assert.Contains("CHUNK", TrimOnlyTags.Refusal(Wave(RiffChunk("bext", new byte[8]), RiffChunk("data", new byte[8]))));
        Assert.Contains("INFO", TrimOnlyTags.Refusal(Wave(InfoList(("ITRK", "5")), RiffChunk("data", new byte[8]))));
    }

    [Fact]
    public void Tags_that_cannot_be_read_leave_the_file_alone_with_the_reason()
    {
        var refusal = TrimOnlyTags.Refusal(MusicTagRead.Unreadable("an APE tag"));

        Assert.Equal("Its tags could not be read in full (an APE tag), so it is not trimmed without converting it.", refusal);
    }

    private static MusicTagSet Set(MusicTagLayout layout, params MusicTag[] tags) => new(layout, tags);

    [Fact]
    public void Tags_are_kept_when_every_source_tag_is_in_the_output_with_its_values()
    {
        var source = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["T"]), new MusicTag("TPE1", ["A"]), new MusicTag("TLEN", ["183000"]), new MusicTag("TSSE", ["LAME"]),
            new MusicTag("TXXX:iTunSMPB", [" 0 1"]));
        // FFmpeg adds its encoder and Optimisarr its marker; TLEN and iTunSMPB are cleared on purpose.
        var output = Set(MusicTagLayout.Id3, new MusicTag("TPE1", ["A"]), new MusicTag("TIT2", ["T"]), new MusicTag("TSSE", ["Lavf61"]), new MusicTag("TXXX:optimisarr", ["v1"]));

        var comparison = TrimOnlyTags.Compare(source, output);

        Assert.True(comparison.Kept, comparison.Detail);
        Assert.Equal("All 2 source tag(s) are unchanged.", comparison.Detail);
    }

    [Fact]
    public void A_lost_frame_fails_and_is_named()
    {
        var source = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["T"]), new MusicTag("POPM", ["abc"]), new MusicTag("UFID", ["def"]));
        var output = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["T"]), new MusicTag("TXXX:lyrics-eng", ["la"]));

        var comparison = TrimOnlyTags.Compare(source, output);

        Assert.False(comparison.Kept);
        Assert.Equal("The tags lost POPM, UFID and gained TXXX:lyrics-eng.", comparison.Detail);
    }

    [Fact]
    public void Values_joined_into_one_or_a_frame_written_twice_fail_as_changed()
    {
        var joined = TrimOnlyTags.Compare(
            Set(MusicTagLayout.Flac, new MusicTag("ARTIST", ["A", "B"])), Set(MusicTagLayout.Flac, new MusicTag("ARTIST", ["A;B"])));
        var doubled = TrimOnlyTags.Compare(
            Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["T"])), Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["T"]), new MusicTag("TIT2", ["T"])));

        Assert.Equal("The tags changed ARTIST.", joined.Detail);
        Assert.False(doubled.Kept);
    }

    [Fact]
    public void Tags_in_another_layout_fail()
    {
        Assert.False(TrimOnlyTags.Compare(Set(MusicTagLayout.Flac), Set(MusicTagLayout.OggOpus)).Kept);
    }

    [Fact]
    public void Text_written_in_another_unicode_encoding_still_compares_equal()
    {
        var source = Mp3(3, Text(3, "TIT2", "Café"), Text(3, "TPE1", "Artist")).Tags!;
        var output = Mp3(3, Frame("TIT2", [1, 0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("Café\0")]), Frame("TPE1", "\0Artist\0"u8.ToArray()),
            Frame("TXXX", "\0optimisarr\0v1\0"u8.ToArray()), Frame("TSSE", "\0Lavf61.7.103\0"u8.ToArray())).Tags!;

        Assert.True(TrimOnlyTags.Compare(source, output).Kept);
    }

    [Fact]
    public void Text_in_a_legacy_character_set_leaves_the_file_alone()
    {
        // FFmpeg would read CP1251 bytes as ISO-8859-1 and write them again as Unicode, which says something else.
        var read = Mp3(3, Frame("TIT2", [0, 0xCA, 0xE8, 0xED, 0xEE]), Frame("TPE1", "\0Artist"u8.ToArray()));

        Assert.Equal("Some of its tags are written in a legacy character set (TIT2), which trimming without converting would rewrite, so it is left alone.",
            TrimOnlyTags.Refusal(read));
    }

    [Fact]
    public void Text_moved_out_of_a_legacy_character_set_fails_as_changed()
    {
        var source = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["Êèíî"], LegacyText: true));
        var output = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["Êèíî"]));

        Assert.Equal("The tags changed TIT2.", TrimOnlyTags.Compare(source, output).Detail);
    }

    [Fact]
    public void A_tag_the_output_gains_fails_unless_it_is_written_again_on_purpose()
    {
        var source = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["Song"]));
        // FFmpeg merged a second ID3v2 tag the reader did not see.
        var output = Set(MusicTagLayout.Id3, new MusicTag("TIT2", ["Song"]), new MusicTag("TPE1", ["Artist"]), new MusicTag("TXXX:lyrics-eng", ["la"]),
            new MusicTag("TSSE", ["Lavf"]), new MusicTag("TXXX:optimisarr", ["v1"]));
        var flac = TrimOnlyTags.Compare(Set(MusicTagLayout.Flac, new MusicTag("TITLE", ["T"])),
            Set(MusicTagLayout.Flac, new MusicTag("TITLE", ["T"]), new MusicTag("ENCODER", ["Lavf"]), new MusicTag("OPTIMISARR", ["v1"]), new MusicTag("ARTIST", ["A"])));

        Assert.Equal("The tags gained TPE1, TXXX:lyrics-eng.", TrimOnlyTags.Compare(source, output).Detail);
        Assert.Equal("The tags gained ARTIST.", flac.Detail);
    }

    private static MusicTagSet Id3v1Only => new(MusicTagLayout.Id3, [
        new("ID3V1:TITLE", ["Title"]), new("ID3V1:ARTIST", ["Artist"]), new("ID3V1:ALBUM", ["Album"]), new("ID3V1:YEAR", ["1999"]),
        new("ID3V1:COMMENT", ["Comment"]), new("ID3V1:TRACK", ["5"]), new("ID3V1:GENRE", ["17"])], Id3Version: null, HasId3v1: true);

    [Fact]
    public void An_id3v1_only_file_may_gain_the_same_fields_as_id3v2_frames()
    {
        // FFmpeg 7.1 reads the ID3v1 fields and writes them as ID3v2.4 frames as well, with the genre by its name.
        var output = new MusicTagSet(MusicTagLayout.Id3, [
            new("TIT2", ["Title"]), new("TPE1", ["Artist"]), new("TALB", ["Album"]), new("TDRC", ["1999"]), new("TXXX:comment", ["Comment"]),
            new("TRCK", ["5"]), new("TCON", ["Rock"]), new("TXXX:optimisarr", ["v1"]), new("TSSE", ["Lavf61.7.103"]), .. Id3v1Only.Tags], 4, true);

        var comparison = TrimOnlyTags.Compare(Id3v1Only, output);

        Assert.True(comparison.Kept, comparison.Detail);
        Assert.False(TrimOnlyTags.Compare(Id3v1Only, output with { Tags = [.. output.Tags.Where(tag => tag.Key != "TCON"), new("TCON", ["Blues"])] }).Kept);
        Assert.False(TrimOnlyTags.Compare(Id3v1Only, output with { Tags = [.. output.Tags, new("TPE2", ["Artist"])] }).Kept);
        // With an ID3v2 tag of its own, the source's frames are the only ones expected.
        Assert.False(TrimOnlyTags.Compare(Id3v1Only with { Id3Version = 4 }, output).Kept);
    }

    [Fact]
    public void A_changed_id3_version_or_a_lost_id3v1_tag_fails()
    {
        var tags = new MusicTag[] { new("TIT2", ["T"]) };
        var v23 = new MusicTagSet(MusicTagLayout.Id3, tags, 3, false);

        Assert.Equal("The ID3v2 tag changed from version 2.3 to 2.4.", TrimOnlyTags.Compare(v23, v23 with { Id3Version = 4 }).Detail);
        Assert.Equal("The ID3v1 tag was lost.", TrimOnlyTags.Compare(v23 with { HasId3v1 = true }, v23).Detail);
        Assert.Equal("An ID3v1 tag was added.", TrimOnlyTags.Compare(v23, v23 with { HasId3v1 = true }).Detail);
        // A file without an ID3v2 tag is given FFmpeg's own 2.4 tag.
        Assert.True(TrimOnlyTags.Compare(v23 with { Tags = [], Id3Version = null }, v23 with { Tags = [], Id3Version = 4 }).Kept);
    }

    [Theory]
    [InlineData("80s Rock")]
    [InlineData("(Rock)")]
    [InlineData("17")]
    [InlineData(" (17)")]
    public void A_genre_ffmpeg_might_read_as_a_number_leaves_the_file_alone(string genre)
    {
        Assert.Contains("TCON", TrimOnlyTags.Refusal(Mp3(4, Text(4, "TCON", genre))));
    }

    [Fact]
    public void A_picture_whose_mime_type_ffmpeg_rewrites_leaves_the_file_alone()
    {
        var jpg = Mp3(3, Frame("APIC", [0, .. "image/jpg\0"u8.ToArray(), 3, 0, 0xFF, 0xD8]));
        var flac = MusicTagReader.Read(".flac", new MusicTagBytes(Flac(FlacBlock(0, new byte[34]), FlacBlock(6, FlacPicture(3, "", [0xFF, 0xD8], "image/jpg"), last: true))));

        Assert.Contains("APIC", TrimOnlyTags.Refusal(jpg));
        Assert.Contains("PICTURE", TrimOnlyTags.Refusal(flac));
        Assert.Null(TrimOnlyTags.Refusal(Mp3(3, Frame("APIC", [0, .. "image/png\0"u8.ToArray(), 3, 0, 0x89, 0x50]))));
    }

    [Fact]
    public void Long_tag_names_are_shortened_so_the_refusal_fits()
    {
        var names = Enumerable.Range(0, 10).Select(index => (new string((char)('A' + index), 900), "a")).Concat(Enumerable.Range(0, 10).Select(index => (new string((char)('A' + index), 900), "b")));

        var refusal = TrimOnlyTags.Refusal(FlacWith([.. names]));

        Assert.NotNull(refusal);
        Assert.True(refusal.Length <= TrimOnlyTags.MaximumRefusalLength, refusal);
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(191, true)]
    [InlineData(255, true)]
    [InlineData(192, false)]
    [InlineData(254, false)]
    public void An_id3v1_genre_ffmpeg_has_no_name_for_is_written_back_as_none(byte genre, bool kept)
    {
        // FFmpeg 7.1 writes the genres it names (0 to 191) back as they were, and every other number as 255.
        var read = MusicTagReader.Read(".mp3", new MusicTagBytes([.. AudioFrames, .. Id3v1("Title", "Artist", 1, genre)]));

        Assert.Equal(kept, TrimOnlyTags.Refusal(read) is null);
    }
}
