using Optimisarr.Api.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafShadowTests
{
    private static QualityMeasurementContext Context => new(1920, 1080, false, false,
        ReferenceDurationSeconds: 600, ReferenceFrameRate: 24, EncodedVideo: new(1920, 1080, 8));

    private static QualityResult Score(QualityMeasurementContext context, int frames = 240) =>
        QualityResult.Ok(new(95, 94, 80, null, null, context.ModelVersion, "test", 90, frames))
            with { DistortedShiftToken = "0.041667" };

    [Fact]
    public void Short_sources_have_one_bounded_window_and_invalid_durations_have_none()
    {
        Assert.Equal(new VmafWindow(0, 5), Assert.Single(VmafShadowPlan.Windows(5.8)));
        Assert.Empty(VmafShadowPlan.Windows(double.PositiveInfinity));
        Assert.Empty(VmafShadowPlan.Windows(-1));
        Assert.Empty(VmafShadowPlan.Windows(double.NaN));
    }

    [Fact]
    public async Task UHD_uses_the_pinned_UHD_model_and_records_actual_encoded_format()
    {
        var calls = new List<QualityMeasurementContext>();
        var context = Context with { ReferenceWidth = 3840, ReferenceHeight = 2160, EncodedVideo = new(3840, 2160, 10) };
        using var service = new VmafShadowService(true, (_, _, measurement, _) =>
        {
            calls.Add(measurement);
            return Task.FromResult(Score(measurement));
        });
        var result = await service.ObserveAsync("source", "output", context, null, default);
        Assert.Equal("vmaf_4k_v0.6.1", result!.BaselineModel);
        Assert.Equal("vmaf_v1.0.16_1d5h_2160", result.CandidateModel);
        Assert.All(calls, call => Assert.Equal(context.EncodedVideo, call.EncodedVideo));
        Assert.Equal(context, result.Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filesystem_failure_is_recorded_and_the_slot_is_reusable(bool accessDenied)
    {
        var fail = true;
        using var service = new VmafShadowService(true, (_, _, context, _) =>
        {
            if (fail && accessDenied) throw new UnauthorizedAccessException("Candidate access was revoked");
            if (fail) throw new IOException("Candidate became unreadable");
            return Task.FromResult(Score(context));
        });
        Assert.Equal("Unavailable", (await service.ObserveAsync("source", "output", Context, null, default))!.Status);
        fail = false;
        Assert.Equal("Measured", (await service.ObserveAsync("source", "output", Context, null, default))!.Status);
    }

    [Fact]
    public async Task Invalid_research_numbers_cannot_break_serialization_of_the_authoritative_report()
    {
        using var service = new VmafShadowService(true, (_, _, context, _) =>
            Task.FromResult(Score(context) with { Scores = Score(context).Scores! with { VmafMean = double.NaN } }));
        var skipped = await service.ObserveAsync("source", "output", Context with { ReferenceDurationSeconds = double.NaN }, null, default);
        var failed = await service.ObserveAsync("source", "output", Context, null, default);
        Assert.Equal("Skipped", skipped!.Status);
        Assert.Equal("Unavailable", failed!.Status);
        Assert.NotEmpty(System.Text.Json.JsonSerializer.Serialize(skipped));
        Assert.NotEmpty(System.Text.Json.JsonSerializer.Serialize(failed));
    }

    [Fact]
    public async Task Disabled_shadow_does_not_measure_or_add_evidence()
    {
        var service = new VmafShadowService(false, (_, _, _, _) => throw new Exception("Must not measure"));
        Assert.Null(await service.ObserveAsync("source", "output", Context, null, default));
    }

    [Fact]
    public async Task Both_models_measure_identical_bounded_windows_and_share_baseline_alignment()
    {
        var calls = new List<QualityMeasurementContext>();
        var service = new VmafShadowService(true, (_, _, context, _) =>
        {
            calls.Add(context);
            return Task.FromResult(Score(context));
        });
        var evidence = await service.ObserveAsync("source", "output", Context, null, default);
        Assert.Equal("Measured", evidence!.Status);
        Assert.Equal(3, evidence.Windows.Count);
        Assert.Equal(6, calls.Count);
        for (var i = 0; i < calls.Count; i += 2)
        {
            Assert.Equal("vmaf_v0.6.1", calls[i].ModelVersion);
            Assert.Equal("vmaf_v1.0.16_3d0h", calls[i + 1].ModelVersion);
            Assert.Equal("0.041667", calls[i + 1].DistortedShiftToken);
            Assert.Equal(calls[i].ReferenceStartSeconds, calls[i + 1].ReferenceStartSeconds);
            Assert.Equal(calls[i].ReferenceStartSeconds, calls[i].DistortedStartSeconds);
            Assert.Equal(10, calls[i].MeasureDurationSeconds);
            Assert.Equal(1, calls[i].FrameSubsample);
            Assert.Equal(VmafAcceleration.None, calls[i].Acceleration);
        }
    }

    [Theory]
    [InlineData("hdr")]
    [InlineData("hfr")]
    [InlineData("unknown-rate")]
    [InlineData("unknown-depth")]
    [InlineData("unknown-duration")]
    [InlineData("preview")]
    public async Task Unsupported_measurements_are_recorded_without_running_tools(string scenario)
    {
        var context = scenario switch
        {
            "hdr" => Context with { ReferenceIsHdr = true },
            "hfr" => Context with { ReferenceFrameRate = 60 },
            "unknown-rate" => Context with { ReferenceFrameRate = null },
            "unknown-depth" => Context with { EncodedVideo = null },
            "unknown-duration" => Context with { ReferenceDurationSeconds = double.NaN },
            _ => Context
        };
        var service = new VmafShadowService(true, (_, _, _, _) => throw new Exception("Must not measure"));
        var evidence = await service.ObserveAsync("source", "output", context,
            scenario == "preview" ? "Disposable previews are excluded." : null, default);
        Assert.Equal("Skipped", evidence!.Status);
        Assert.NotEmpty(evidence.Detail!);
        Assert.Empty(evidence.Windows);
    }

    [Fact]
    public async Task Missing_model_keeps_baseline_evidence_and_does_not_throw()
    {
        var service = new VmafShadowService(true, (_, _, context, _) => Task.FromResult(
            QualityScoreCommandBuilder.IsV1(context.ModelVersion) ? QualityResult.Failed("Model unavailable") : Score(context)));
        var result = await service.ObserveAsync("source", "output", Context, null, default);
        Assert.Equal("Unavailable", result!.Status);
        Assert.Single(result.Windows);
        Assert.NotNull(result.Windows[0].Baseline);
        Assert.Null(result.Windows[0].Candidate);
    }

    [Fact]
    public async Task Different_frame_counts_are_not_valid_pairs()
    {
        var service = new VmafShadowService(true, (_, _, context, _) =>
            Task.FromResult(Score(context, QualityScoreCommandBuilder.IsV1(context.ModelVersion) ? 239 : 240)));
        var result = await service.ObserveAsync("source", "output", Context, null, default);
        Assert.Equal("Unavailable", result!.Status);
        Assert.Contains("frame", result.Detail!);
    }

    [Fact]
    public async Task Observation_timeout_preserves_completed_pairs_and_releases_the_slot()
    {
        var calls = 0;
        var service = new VmafShadowService(true, async (_, _, context, token) =>
        {
            if (++calls == 3) await Task.Delay(Timeout.Infinite, token);
            return Score(context);
        }, TimeSpan.FromMilliseconds(100));
        var result = await service.ObserveAsync("source", "output", Context, null, default);
        Assert.Equal("TimedOut", result!.Status);
        Assert.Single(result.Windows);
        Assert.Equal("Measured", (await service.ObserveAsync("source", "output", Context, null, default))!.Status);
    }

    [Fact]
    public async Task Busy_observer_skips_instead_of_queuing_more_work()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new VmafShadowService(true, async (_, _, context, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Score(context);
        });
        var first = service.ObserveAsync("source", "output", Context, null, default);
        await entered.Task;
        Assert.Equal("Skipped", (await service.ObserveAsync("source", "output", Context, null, default))!.Status);
        release.SetResult();
        Assert.Equal("Measured", (await first)!.Status);
    }

    [Fact]
    public async Task Job_cancellation_still_cancels_the_job_and_releases_the_slot()
    {
        using var stop = new CancellationTokenSource();
        var service = new VmafShadowService(true, async (_, _, context, token) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Score(context);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ObserveAsync("source", "output", Context, null, stop.Token));
    }

    [Fact]
    public async Task Research_scores_and_failures_cannot_change_verification_verdicts()
    {
        var service = new VmafShadowService(true, (_, _, context, _) => Task.FromResult(Score(context)));
        var evidence = await service.ObserveAsync("source", "output", Context, null, default);
        var passed = new VerificationReport([new("Quality", CheckOutcome.Passed, "Baseline passed")]);
        var failed = new VerificationReport([new("Quality", CheckOutcome.Failed, "Baseline failed")]);
        Assert.True((passed with { ShadowVmaf = evidence }).Passed);
        Assert.False((failed with { ShadowVmaf = evidence }).Passed);
        Assert.True((passed with { ShadowVmaf = evidence! with { Status = "Unavailable" } }).Passed);
    }
}
