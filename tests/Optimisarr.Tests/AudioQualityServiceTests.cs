using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class AudioQualityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-quality-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _called = [];
    private string Reference => Path.Combine(_root, "reference.wav");
    private string Candidate => Path.Combine(_root, "candidate.opus");
    private string Tool(string name) => Path.Combine(_root, name);

    public AudioQualityServiceTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var path in new[] { Reference, Candidate, Tool("ffmpeg"), Tool("ffprobe"), Tool("metric") })
            File.WriteAllText(path, path);
    }

    private AudioQualityService Service(Func<string, IReadOnlyList<string>, CancellationToken, TimeSpan?, Task<ToolProcessResult>>? runner = null) =>
        new(Tool("ffmpeg"), Tool("ffprobe"), Tool("metric"), _root, runner ?? Run);

    private Task<ToolProcessResult> Run(string command, IReadOnlyList<string> args, CancellationToken token, TimeSpan? timeout)
    {
        token.ThrowIfCancellationRequested();
        _called.Add(Path.GetFileName(command));
        Assert.NotNull(timeout);
        if (command == Tool("ffprobe"))
            return Task.FromResult(new ToolProcessResult(0, """{"streams":[{"codec_type":"audio","channels":2,"sample_rate":"48000","duration":"3"}],"format":{"duration":"3"}}""", null));
        if (command == Tool("ffmpeg"))
        {
            Assert.Contains("-n", args);
            File.WriteAllBytes(args[^1], new byte[144000 * 2 * 4]);
            return Task.FromResult(new ToolProcessResult(0, "", null));
        }
        return Task.FromResult(new ToolProcessResult(0,
            $$"""{"schema":1,"metric":"zimtohrli","revision":"{{AudioQualityResultParser.Revision}}","sampleRate":48000,"channels":2,"frames":144000,"fullScaleSineDb":78.3,"distances":[0.01,0.02]}""", null));
    }


    [Theory]
    [InlineData(13, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task Selected_soundtracks_allow_only_bounded_resampler_rounding_without_padding_missing_audio(int shortfall, bool passes)
    {
        async Task<ToolProcessResult> Runner(string c, IReadOnlyList<string> args, CancellationToken token, TimeSpan? timeout)
        {
            var result = await Run(c, args, token, timeout);
            if (c == Tool("ffprobe")) return new(0, SoundtrackQualityTests.Probe(SoundtrackQualityTests.Track()), null);
            if (c == Tool("ffmpeg")) File.WriteAllBytes(args[^1], new byte[139200 * 8]);
            if (c == Tool("ffmpeg") && args.Contains(Candidate)) File.WriteAllBytes(args[^1], new byte[(139200 - shortfall) * 8]);
            if (c == Tool("metric"))
            {
                Assert.Equal(new FileInfo(args[0]).Length, new FileInfo(args[1]).Length);
                Assert.Equal((139200 - shortfall) * 8, new FileInfo(args[0]).Length);
                return result with { Output = result.Output.Replace("\"frames\":144000", $"\"frames\":{139200 - shortfall}") };
            }
            return result;
        }
        var measured = await Service(Runner).MeasureTrackAsync(Reference, Candidate, 0, 0, default);
        Assert.Equal(passes, measured.Measured);
        if (!passes) Assert.DoesNotContain("metric", _called);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Successful_measurement_binds_files_tools_coverage_and_cleans_only_owned_scratch()
    {
        var keep = Path.Combine(_root, "unrelated.raw");
        await File.WriteAllTextAsync(keep, "keep");
        var result = await Service().MeasureAsync(Reference, Candidate, default);
        Assert.True(result.Measured, result.Error);
        Assert.Single(result.Windows);
        Assert.False(result.Sampled);
        Assert.Equal(64, result.ReferenceSha256!.Length);
        Assert.Equal(64, result.MetricSha256!.Length);
        Assert.Equal(0.02, result.WorstChannelDistance);
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
        Assert.Equal(Reference, await File.ReadAllTextAsync(Reference));
    }

    [Fact]
    public async Task Nonzero_tool_exit_or_partial_channel_evidence_never_reports_measured()
    {
        foreach (var output in new[] { new ToolProcessResult(1, "{}", "failed"), new ToolProcessResult(0, "{}", null) })
        {
            var result = await Service((c, a, t, d) => c == Tool("metric") ? Task.FromResult(output) : Run(c, a, t, d))
                .MeasureAsync(Reference, Candidate, default);
            Assert.False(result.Measured);
            Assert.Null(result.WorstChannelDistance);
            Assert.NotNull(result.Error);
            Assert.Empty(Directory.GetDirectories(_root));
        }
    }

    [Fact]
    public async Task Changing_input_during_measurement_invalidates_all_scores()
    {
        var result = await Service(async (c, a, t, d) =>
        {
            var output = await Run(c, a, t, d);
            if (c == Tool("metric")) await File.AppendAllTextAsync(Candidate, "changed", t);
            return output;
        }).MeasureAsync(Reference, Candidate, default);
        Assert.False(result.Measured);
        Assert.Null(result.WorstChannelDistance);
        Assert.Contains("changed", result.Error!);
    }

    [Fact]
    public async Task Changing_the_tool_or_truncating_prepared_pcm_invalidates_the_measurement()
    {
        var changed = await Service(async (c, a, t, d) =>
        {
            var output = await Run(c, a, t, d);
            if (c == Tool("metric")) await File.AppendAllTextAsync(Tool("metric"), "changed", t);
            return output;
        }).MeasureAsync(Reference, Candidate, default);
        Assert.False(changed.Measured);
        _called.Clear();
        var truncated = await Service(async (c, a, t, d) =>
        {
            var output = await Run(c, a, t, d);
            if (c == Tool("ffmpeg")) await File.WriteAllBytesAsync(a[^1], new byte[192000], t);
            return output;
        }).MeasureAsync(Reference, Candidate, default);
        Assert.False(truncated.Measured);
        Assert.DoesNotContain("metric", _called);
    }

    [Fact]
    public async Task Concurrent_assessments_use_distinct_scratch_directories()
    {
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task<ToolProcessResult> Runner(string c, IReadOnlyList<string> a, CancellationToken t, TimeSpan? d)
        {
            if (c == Tool("ffmpeg")) paths.Add(Path.GetDirectoryName(a[^1])!);
            await Task.Yield();
            // The fake runner's call log is not thread safe; only record scratch here.
            lock (_called) return Run(c, a, t, d).GetAwaiter().GetResult();
        }
        var results = await Task.WhenAll(Service(Runner).MeasureAsync(Reference, Candidate, default), Service(Runner).MeasureAsync(Reference, Candidate, default));
        Assert.All(results, result => Assert.True(result.Measured, result.Error));
        Assert.Equal(2, paths.Distinct().Count());
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Cancellation_does_not_start_work_and_removes_owned_scratch_on_abort()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().MeasureAsync(Reference, Candidate, stop.Token));
        Assert.Empty(_called);
        using var during = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(async (c, a, t, d) =>
        {
            var output = await Run(c, a, t, d);
            if (c == Tool("ffmpeg")) during.Cancel();
            return output;
        }).MeasureAsync(Reference, Candidate, during.Token));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Probe_errors_stop_before_decoding()
    {
        var result = await Service((c, a, t, d) => Task.FromResult(new ToolProcessResult(0, "{}", null)))
            .MeasureAsync(Reference, Candidate, default);
        Assert.False(result.Measured);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Already_cancelled_native_process_request_is_rejected_before_launch()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedToolProcess.RunAsync(Tool("missing-executable"), [], stop.Token));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
