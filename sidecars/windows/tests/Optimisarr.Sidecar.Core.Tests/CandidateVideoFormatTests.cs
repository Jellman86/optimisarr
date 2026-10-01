using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class CandidateVideoFormatTests
{
    private static readonly string[] Command = ["-lavfi", "model=v1:cambi.enc_width={{encodedWidth}}:cambi.enc_height={{encodedHeight}}:cambi.enc_bitdepth={{encodedBitDepth}}"];

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuv420p10le", 10)]
    public void CAMBI_uses_actual_candidate_geometry_and_depth(string format, int depth)
    {
        var json = $$"""{"streams":[{"codec_type":"video","width":1280,"height":720,"pix_fmt":"{{format}}","bits_per_raw_sample":"0"}]}""";
        var resolved = CandidateVideoFormat.Resolve(Command, json);
        Assert.NotNull(resolved);
        Assert.Contains($"cambi.enc_width=1280:cambi.enc_height=720:cambi.enc_bitdepth={depth}", resolved[1]);
        Assert.DoesNotContain("{{", resolved[1]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":0,\"height\":720,\"pix_fmt\":\"yuv420p\"}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p12le\"}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"unknown\"}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p\",\"bits_per_raw_sample\":\"10\"}]}")]
    public void Missing_invalid_or_inconsistent_candidate_format_cannot_be_scored(string json) =>
        Assert.Null(CandidateVideoFormat.Resolve(Command, json));

    [Theory]
    [InlineData("invalid")]
    [InlineData("-1")]
    [InlineData("8.5")]
    public void An_invalid_explicit_depth_cannot_be_ignored(string rawDepth)
    {
        var json = $$"""{"streams":[{"codec_type":"video","width":1280,"height":720,"pix_fmt":"yuv420p","bits_per_raw_sample":"{{rawDepth}}"}]}""";
        Assert.Null(CandidateVideoFormat.Resolve(Command, json));
    }

    [Fact]
    public void Legacy_commands_do_not_require_a_new_probe() =>
        Assert.Equal(new[] { "legacy" }, CandidateVideoFormat.Resolve(["legacy"], null));

    [Fact]
    public void Cover_art_is_not_the_candidate_picture()
    {
        var json = """{"streams":[{"codec_type":"video","width":100,"height":100,"pix_fmt":"yuv420p","disposition":{"attached_pic":1}},{"codec_type":"video","width":1280,"height":720,"pix_fmt":"yuv420p"}]}""";
        Assert.Contains("enc_width=1280", CandidateVideoFormat.Resolve(Command, json)![1]);
    }
}
