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
        Assert.Equal("The tags lost POPM, UFID.", comparison.Detail);
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
    public void Text_written_in_another_encoding_or_id3_version_still_compares_equal()
    {
        var source = Mp3(3, Text(3, "TIT2", "Café"), Text(3, "TPE1", "Artist")).Tags!;
        var output = Mp3(3, Frame("TIT2", [1, 0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("Café\0")]), Frame("TPE1", "\0Artist\0"u8.ToArray()),
            Frame("TXXX", "\0optimisarr\0v1\0"u8.ToArray()), Frame("TSSE", "\0Lavf61.7.103\0"u8.ToArray())).Tags!;

        Assert.True(TrimOnlyTags.Compare(source, output).Kept);
    }
}
