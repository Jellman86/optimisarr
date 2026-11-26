using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class KeptAudioComparisonTests
{
    // Captured from the shipped FFmpeg 6.1: -f framehash of a stream-copied Opus cut. The first
    // packet carries skip-samples side data after its own hash.
    private const string FrameHashes = """
        #format: frame checksums
        #version: 2
        #hash: SHA256
        #extradata 0,                              19, 7bd1
        #tb 0: 1/48000
        #media_type 0: audio
        #stream#, dts,        pts, duration,     size, hash
        0,       -312,       -312,      960,      160, fecccc70, S=1,       10, 7bd11a20
        0,        648,        648,      960,      160, 6d992220
        0,       1608,       1608,      960,      159, d1bc91e3
        """;

    [Fact]
    public void Frame_hashes_are_read_per_packet_ignoring_headers_and_side_data()
    {
        Assert.Equal(["fecccc70", "6d992220", "d1bc91e3"], FrameHashParser.Parse(FrameHashes));
    }

    [Fact]
    public void A_frame_hash_listing_with_a_malformed_line_is_not_trusted()
    {
        Assert.Null(FrameHashParser.Parse("0,  0,  0,  960\n"));
        Assert.Null(FrameHashParser.Parse("#only a header\n"));
    }

    [Fact]
    public void The_hash_muxer_reads_one_digest()
    {
        Assert.Equal("1b29e60e", HashMuxerParser.Parse("SHA256=1b29e60e\n"));
        Assert.Null(HashMuxerParser.Parse("MD5=abc\n"));
        Assert.Null(HashMuxerParser.Parse(""));
    }

    [Theory]
    [InlineData(new[] { "c", "d", "e" }, true)]
    [InlineData(new[] { "a", "b" }, true)]
    [InlineData(new[] { "e", "f" }, true)]
    // Repeated silent packets put the first output packet in several places.
    [InlineData(new[] { "s", "s", "x" }, true)]
    [InlineData(new[] { "c", "e" }, false)]
    [InlineData(new[] { "e", "f", "g" }, false)]
    [InlineData(new[] { "c", "D", "e" }, false)]
    [InlineData(new string[0], false)]
    public void A_copy_must_be_one_unbroken_run_of_the_source_packets(string[] output, bool expected)
    {
        string[] source = ["a", "b", "s", "s", "s", "x", "c", "d", "e", "f"];

        Assert.Equal(expected, PacketRun.IsContiguousRunOf(output, source));
    }

    [Fact]
    public void The_kept_window_of_the_source_is_decoded_to_doubles_so_every_sample_format_compares_exactly()
    {
        var trim = new AudioTrimPlan(121_275, 584_325, 44_100, 16, -60);

        Assert.Equal(["-hide_banner", "-nostdin", "-v", "error", "-i", "/music/a b.flac", "-map", "0:a:0",
            "-af", "atrim=start_sample=121275:end_sample=584325,asetpts=PTS-STARTPTS",
            "-c:a", "pcm_f64le", "-f", "hash", "-hash", "sha256", "-"], KeptAudioCommandBuilder.DecodedDigest("/music/a b.flac", trim));
        Assert.Equal(["-hide_banner", "-nostdin", "-v", "error", "-i", "/work/a b.flac", "-map", "0:a:0",
            "-c:a", "pcm_f64le", "-f", "hash", "-hash", "sha256", "-"], KeptAudioCommandBuilder.DecodedDigest("/work/a b.flac", null));
    }

    [Fact]
    public void Packets_are_hashed_as_they_are_stored_without_decoding()
    {
        Assert.Equal(["-hide_banner", "-nostdin", "-v", "error", "-i", "/music/a.mp3", "-map", "0:a:0",
            "-c", "copy", "-f", "framehash", "-hash", "sha256", "-"], KeptAudioCommandBuilder.PacketDigests("/music/a.mp3"));
    }
}
