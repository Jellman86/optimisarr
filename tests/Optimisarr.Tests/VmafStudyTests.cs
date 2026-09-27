namespace Optimisarr.Tests;

public sealed class VmafStudyTests
{
    [Theory]
    [InlineData("--typo")]
    [InlineData("--qualities", "")]
    [InlineData("--qualities", "18,18")]
    [InlineData("--qualities", "abc")]
    [InlineData("--qualities", "-1")]
    [InlineData("--preset")]
    public void Invalid_options_fail_before_starting_work(params string[] extra)
    {
        Assert.Null(StudyOptions.Parse(["--ffmpeg", "ffmpeg", "--ffprobe", "ffprobe", "--out", "out", "source", .. extra]));
    }

    [Fact]
    public void Scoring_can_use_a_different_binary_from_hardware_encoding()
    {
        var options = StudyOptions.Parse(["--ffmpeg", "encode", "--measurement-ffmpeg", "measure", "--ffprobe", "probe", "--out", "out", "source"]);
        Assert.Equal("measure", options!.MeasurementFfmpeg);
        Assert.Equal("encode", options.Ffmpeg);
    }

    [Fact]
    public void A_cache_key_changes_with_source_toolchain_or_encode_arguments_but_not_the_scoring_model()
    {
        var key = StudyIntegrity.ClipKey("source-hash", "ffmpeg-hash", ["-c:v", "libx265", "-preset", "fast"]);
        Assert.Equal(key, StudyIntegrity.ClipKey("source-hash", "ffmpeg-hash", ["-c:v", "libx265", "-preset", "fast"]));
        Assert.NotEqual(key, StudyIntegrity.ClipKey("other-source", "ffmpeg-hash", ["-c:v", "libx265", "-preset", "fast"]));
        Assert.NotEqual(key, StudyIntegrity.ClipKey("source-hash", "other-tool", ["-c:v", "libx265", "-preset", "fast"]));
        Assert.NotEqual(key, StudyIntegrity.ClipKey("source-hash", "ffmpeg-hash", ["-c:v", "libx265", "-preset", "slow"]));
    }

    private static StudyRow Row(string model) => new("source", 0, "libx265", 22, 1000, model, 95, 90, 80, 96, 100, null);

    [Fact]
    public void A_failed_empty_or_incomplete_run_cannot_be_reported_as_complete()
    {
        var baseline = Row(StudyOptions.BaselineHd);
        var candidate = Row(StudyOptions.DefaultCandidateHd);
        Assert.False(StudyIntegrity.Complete([], 0));
        Assert.False(StudyIntegrity.Complete([baseline], 2));
        Assert.False(StudyIntegrity.Complete([baseline, candidate with { Error = "unsupported model" }], 2));
        Assert.False(StudyIntegrity.Complete([baseline, candidate with { Frames = 99 }], 2));
        Assert.False(StudyIntegrity.Complete([baseline, candidate with { Encoder = "hevc_qsv" }], 2));
        Assert.False(StudyIntegrity.Complete([baseline, candidate with { Harmonic = double.NaN }], 2));
        Assert.False(StudyIntegrity.Complete([baseline, candidate with { Bytes = 0 }], 2));
        Assert.False(StudyIntegrity.Complete([baseline, baseline], 2));
        Assert.True(StudyIntegrity.Complete([baseline, candidate], 2));
    }

    [Fact]
    public void Reports_never_pair_different_encoders_or_frame_counts()
    {
        var baseline = Row(StudyOptions.BaselineHd);
        var candidate = Row(StudyOptions.DefaultCandidateHd);
        Assert.Empty(StudyIntegrity.Pairs([baseline, candidate with { Encoder = "hevc_qsv" }]));
        Assert.Empty(StudyIntegrity.Pairs([baseline, candidate with { Frames = 99 }]));
        Assert.Empty(StudyIntegrity.Pairs([baseline with { ClipSha256 = "one" }, candidate with { ClipSha256 = "another" }]));
        Assert.Single(StudyIntegrity.Pairs([baseline, candidate]));
    }

    [Fact]
    public void CSV_round_trips_multiline_errors_and_quoted_source_names()
    {
        var row = Row(StudyOptions.BaselineHd) with { Source = "title, \"episode\"", Error = "first line\nsecond, \"quoted\" line" };
        Assert.Equal(row, Assert.Single(StudyRow.Parse(StudyRow.Csv([row]))));
    }

    [Fact]
    public async Task A_verified_cache_is_reused_but_changed_or_partial_clips_are_never_trusted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clip = Path.Combine(root, "clip.mkv");
            await File.WriteAllTextAsync(clip, "encoded bytes");
            await File.WriteAllTextAsync(clip + ".sha256", await StudyRunner.HashAsync(clip, default));
            var missingEncoder = Path.Combine(root, "missing-encoder");
            await StudyRunner.EnsureClipAsync(missingEncoder, [], clip, default);
            await File.WriteAllTextAsync(clip, "changed bytes");
            await File.WriteAllTextAsync(clip + ".partial.mkv", "interrupted encode");
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => StudyRunner.EnsureClipAsync(missingEncoder, [], clip, default));
            Assert.False(File.Exists(clip + ".partial.mkv"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_cancelled_process_is_never_started()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StudyRunner.RunProcessAsync("missing-encoder", [], stop.Token));
    }

    [Fact]
    public async Task A_missing_tool_leaves_an_explicit_incomplete_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var options = StudyOptions.Parse(["--ffmpeg", Path.Combine(root, "missing-tool"), "--ffprobe", "missing-probe", "--out", root, "source"]);
            await Assert.ThrowsAsync<FileNotFoundException>(() => StudyRunner.RunAsync(options!, default));
            using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "run.json")));
            Assert.False(manifest.RootElement.GetProperty("completed").GetBoolean());
            Assert.NotEmpty(manifest.RootElement.GetProperty("errors").EnumerateArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
