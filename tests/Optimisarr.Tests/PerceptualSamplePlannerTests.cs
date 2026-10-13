using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualSamplePlannerTests
{
    [Theory]
    [InlineData(1.5)]
    [InlineData(29.99)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(90.1)]
    [InlineData(3600)]
    public void Audio_samples_are_bounded_disjoint_and_inside_the_primary_audio_duration(double duration)
    {
        var windows = PerceptualSamplePlanner.AudioWindows(duration);
        Assert.InRange(windows.Count, 1, 3);
        Assert.True(windows.Sum(w => w.DurationSeconds) <= 90);
        foreach (var window in windows)
        {
            Assert.InRange(window.DurationSeconds, 1, 30);
            Assert.InRange(window.StartSeconds + window.DurationSeconds, 1, duration);
        }
        for (var index = 1; index < windows.Count; index++)
            Assert.True(windows[index].StartSeconds >= windows[index - 1].StartSeconds + windows[index - 1].DurationSeconds);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(86401)]
    public void Unsupported_durations_never_get_invented_windows(double duration) =>
        Assert.Empty(PerceptualSamplePlanner.AudioWindows(duration));

    [Fact]
    public void Reference_command_preserves_channels_and_gain_and_refuses_overwriting()
    {
        var arguments = PerceptualSamplePlanner.AudioReference("source with spaces.flac", "reference.wav", new(60, 30));
        Assert.Contains("-n", arguments);
        Assert.DoesNotContain("-y", arguments);
        Assert.DoesNotContain("-ac", arguments);
        Assert.DoesNotContain("loudnorm", arguments);
        Assert.Contains("pcm_f32le", arguments);
        Assert.Contains("0:a:0", arguments);
        Assert.Contains("source with spaces.flac", arguments);
        Assert.Equal("reference.wav", arguments[^1]);
    }

    [Fact]
    public void Candidate_commands_use_the_actual_encoder_and_do_not_chain_lossy_candidates()
    {
        var spec = new TranscodeSpec("original.flac", "candidate.opus", null, null, null, false,
            Kind: MediaKind.Audio, AudioEncoder: "libopus", AudioBitrateKbps: 128);
        var sample = PerceptualSamplePlanner.AudioCandidate(spec, "probe.opus", 96, new(120, 30));
        Assert.Equal("original.flac", sample.InputPath);
        Assert.Equal("libopus", sample.AudioEncoder);
        Assert.Equal(96, sample.AudioBitrateKbps);
        Assert.Equal(120, sample.ClipStartSeconds);
        Assert.Equal(30, sample.ClipSeconds);
    }

    [Fact]
    public void Audio_probes_measure_audio_without_repeatedly_encoding_artwork_or_lyrics()
    {
        var spec = new TranscodeSpec("source.flac", "final.mp3", null, null, null, false,
            Kind: MediaKind.Audio, AudioEncoder: "libmp3lame", AudioBitrateKbps: 128);
        var sample = PerceptualSamplePlanner.AudioCandidate(spec, "sample.mp3", 96, new(0, 30));
        var arguments = FfmpegCommandBuilder.Build(sample);
        Assert.DoesNotContain("0:v?", arguments);
        Assert.Contains("0:a", arguments);
        Assert.Contains("libmp3lame", arguments);
        Assert.Contains("96k", arguments);
        Assert.Contains("0:v?", FfmpegCommandBuilder.Build(spec));
        var m4a = spec with { OutputPath = "final.m4a", AudioEncoder = "aac" };
        Assert.Contains("0:s?", FfmpegCommandBuilder.Build(m4a));
        Assert.DoesNotContain("0:s?", FfmpegCommandBuilder.Build(PerceptualSamplePlanner.AudioCandidate(m4a, "sample.m4a", 96, new(0, 30))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(65)]
    public void Reference_and_candidate_clips_decode_context_before_the_same_exact_audio_cut(int start)
    {
        var spec = new TranscodeSpec("original.m4a", "final.opus", null, null, null, false,
            Kind: MediaKind.Audio, AudioEncoder: "libopus", AudioBitrateKbps: 128);
        foreach (var args in new[] { PerceptualSamplePlanner.AudioReference(spec.InputPath, "reference.wav", new(start, 30)),
            FfmpegCommandBuilder.Build(PerceptualSamplePlanner.AudioCandidate(spec, "probe.opus", 96, new(start, 30))) })
        {
            var values = args.ToList();
            var input = values.IndexOf("-i");
            var seeks = values.Select((arg, index) => (arg, index)).Where(p => p.arg == "-ss").Select(p => p.index).ToArray();
            Assert.Equal(start == 0 ? 0 : start > 10 ? 2 : 1, seeks.Length);
            if (start > 0) { Assert.True(seeks[^1] > input); Assert.Equal(Math.Min(start, 10).ToString(), values[seeks[^1] + 1]); }
        }
    }
}
