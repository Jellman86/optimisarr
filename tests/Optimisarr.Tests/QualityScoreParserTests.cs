using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class QualityScoreParserTests
{
    [Fact]
    public void Parses_vmaf_psnr_and_ssim_from_pooled_metrics()
    {
        const string json = """
        {
          "frames": [ { "frameNum": 0, "metrics": { "vmaf": 96.0 } } ],
          "pooled_metrics": {
            "vmaf": { "min": 80.2, "max": 99.1, "mean": 95.3, "harmonic_mean": 94.8 },
            "psnr_y": { "min": 40.0, "max": 48.0, "mean": 45.1, "harmonic_mean": 44.9 },
            "float_ssim": { "min": 0.98, "max": 0.999, "mean": 0.997, "harmonic_mean": 0.996 }
          }
        }
        """;

        var scores = QualityScoreParser.Parse(json);

        Assert.NotNull(scores);
        Assert.Equal(95.3, scores.VmafMean);
        Assert.Equal(94.8, scores.VmafHarmonicMean);
        Assert.Equal(80.2, scores.VmafMin);
        Assert.Equal(96.0, scores.VmafFifthPercentile);
        Assert.Equal(1, scores.FrameCount);
        Assert.Equal(45.1, scores.PsnrYMean);
        Assert.Equal(0.997, scores.SsimMean);
    }

    [Fact]
    public void Calculates_the_fifth_percentile_from_per_frame_scores()
    {
        var frames = string.Join(",", Enumerable.Range(1, 100)
            .Select(score => $$"""{ "frameNum": {{score - 1}}, "metrics": { "vmaf": {{score}} } }"""));
        var json = $$"""
        {
          "frames": [{{frames}}],
          "pooled_metrics": { "vmaf": { "min": 1, "mean": 50.5, "harmonic_mean": 19.27 } }
        }
        """;

        var scores = QualityScoreParser.Parse(json);

        Assert.NotNull(scores);
        Assert.Equal(5.95, scores.VmafFifthPercentile!.Value, precision: 2);
        Assert.Equal(100, scores.FrameCount);
    }

    [Fact]
    public void Parses_vmaf_only_when_psnr_and_ssim_are_absent()
    {
        const string json = """
        { "pooled_metrics": { "vmaf": { "min": 88.0, "mean": 96.0, "harmonic_mean": 95.5 } } }
        """;

        var scores = QualityScoreParser.Parse(json);

        Assert.NotNull(scores);
        Assert.Equal(95.5, scores.VmafHarmonicMean);
        Assert.Null(scores.PsnrYMean);
        Assert.Null(scores.SsimMean);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"pooled_metrics\": {} }")]
    [InlineData("{ \"pooled_metrics\": { \"psnr_y\": { \"mean\": 45.0 } } }")]
    public void Returns_null_when_there_is_no_usable_vmaf(string json)
    {
        Assert.Null(QualityScoreParser.Parse(json));
    }
}
