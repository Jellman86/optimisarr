using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImagePerceptualQualityTests
{
    internal const string Native = """
        {"schema":1,"metric":"ssimulacra2","revision":"a7a9c787341cf703dede03c2009fa460cae5e5df","preparation":"sdr-srgb-native-still-v1","width":96,"height":64,"alpha":false,"maximumAlphaError":0,"score":85,"backgroundScores":[85]}
        """;

    [Fact]
    public void New_controls_are_off_and_saved_ssim_is_independent()
    {
        var policy = VerificationPolicy.Default;
        Assert.False(policy.ImagePerceptualReportingEnabled);
        Assert.False(policy.ImagePerceptualGateEnabled);
        Assert.Null(policy.MinimumImagePerceptualScore);
        Assert.True(policy.ImageQualityGateEnabled);
        var resolved = VerificationPolicyResolver.Resolve(policy,
            new(null, null, null, null, null, null, ImagePerceptualGateEnabled: true, MinimumImagePerceptualScore: 82));
        Assert.True(resolved.RequiresImagePerceptualQuality(MediaKind.Image));
        Assert.False(resolved.RequiresImagePerceptualQuality(MediaKind.Video));
        Assert.Equal(policy.MinimumImageSsim, resolved.MinimumImageSsim);
    }

    [Fact]
    public void Parser_requires_the_pinned_metric_preparation_and_complete_bounded_coverage()
    {
        Assert.Equal(85, ImagePerceptualResultParser.Parse(Native)!.Score);
        foreach (var (from, to) in new[] { ("a7a9c787", "deadbeef"), ("still-v1", "still-v2"),
            ("\"width\":96", "\"width\":0"), ("\"width\":96", "\"width\":16000001"),
            ("\"score\":85", "\"score\":101"), ("\"score\":85", "\"score\":1e400"),
            ("\"backgroundScores\":[85]", "\"backgroundScores\":[]"),
            ("\"alpha\":false", "\"alpha\":true"),
            ("\"maximumAlphaError\":0", "\"maximumAlphaError\":-1"),
            ("\"schema\":1", "\"schema\":1,\"schema\":1") })
            Assert.Null(ImagePerceptualResultParser.Parse(Native.Replace(from, to)));
        Assert.Null(ImagePerceptualResultParser.Parse(new string('x', 8193)));
        Assert.Equal(-50, ImagePerceptualResultParser.Parse(Native.Replace("85", "-50"))!.Score);
    }

    [Fact]
    public void Gate_uses_the_worst_background_and_independently_requires_unchanged_alpha()
    {
        var policy = VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 80 };
        var report = Report(80);
        Assert.True(ImagePerceptualQualityGate.Apply(report, MediaKind.Image, policy).Passed);
        Assert.False(ImagePerceptualQualityGate.Apply(Report(79.999), MediaKind.Image, policy).Passed);
        var alpha = new ImagePerceptualMeasurement(96, 64, true, 0.5, 100, [100, 100]);
        Assert.False(ImagePerceptualQualityGate.Apply(report with { ImagePerceptualQuality = report.ImagePerceptualQuality! with { Measurement = alpha } }, MediaKind.Image, policy).Passed);
        Assert.False(ImagePerceptualQualityGate.Apply(report with { ImagePerceptualQuality = null }, MediaKind.Image, policy).Passed);
        Assert.False(ImagePerceptualQualityGate.Apply(report with { ImagePerceptualQuality = report.ImagePerceptualQuality! with { Error = "Unavailable" } }, MediaKind.Image, policy).Passed);
        Assert.Same(report, ImagePerceptualQualityGate.Apply(report, MediaKind.Image, VerificationPolicy.Default));
        Assert.Same(report, ImagePerceptualQualityGate.Apply(report, MediaKind.Video, policy));
        Assert.Same(report, ImagePerceptualQualityGate.Apply(report, MediaKind.Image, policy, preview: true));
        Assert.False(ImagePerceptualQualityGate.Apply(report with { Checks = [new("Decode", CheckOutcome.Failed, "Damage")] }, MediaKind.Image, policy).Passed);
        foreach (var invalid in new double?[] { null, double.NaN, -1, 101 })
            Assert.False(ImagePerceptualQualityGate.Apply(report, MediaKind.Image, policy with { MinimumImagePerceptualScore = invalid }).Passed);
    }

    [Fact]
    public async Task Measurement_binds_files_and_tool_and_detects_changes_without_scratch()
    {
        var calls = 0;
        var hashes = 0;
        var service = new ImagePerceptualQualityService("/metric",
            (_, args, _, timeout) => {
                calls++;
                Assert.Equal(new[] { Path.GetFullPath("/source '[ ü.png"), Path.GetFullPath("/candidate.webp") }, args);
                Assert.Equal(TimeSpan.FromSeconds(90), timeout);
                return Task.FromResult(new ToolProcessResult(0, Native, null));
            }, (_, _) => Task.FromResult(new string(++hashes > 3 ? 'b' : 'a', 64)));
        var result = await service.MeasureAsync("/source '[ ü.png", "/candidate.webp", CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Null(result.Measurement);
        Assert.Contains("changed", result.Error);
        service = new ImagePerceptualQualityService("/metric", (_, _, _, _) => Task.FromResult(new ToolProcessResult(0, Native, null)),
            (_, _) => Task.FromResult(new string('a', 64)));
        result = await service.MeasureAsync("/source.png", "/candidate.webp", CancellationToken.None);
        Assert.Equal(85, result.Measurement!.Score);
        Assert.Equal(new string('a', 64), result.MetricSha256);
    }

    [Fact]
    public void Diagnostics_retain_numeric_image_evidence_without_raw_errors_or_paths()
    {
        var policy = VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 80 };
        var report = ImagePerceptualQualityGate.Apply(Report(85), MediaKind.Image, policy);
        var snapshot = DiagnosticVerificationSnapshot.Read(JsonSerializer.Serialize(report));
        Assert.NotNull(snapshot);
        Assert.Equal(85, snapshot.ImagePerceptualScore);
        Assert.Equal(80, snapshot.MinimumImagePerceptualScore);
        Assert.Equal(0, snapshot.MaximumImageAlphaError);
        Assert.Contains(snapshot.Checks, check => check.Name == ImagePerceptualQualityGate.CheckName);
        Assert.Null(DiagnosticVerificationSnapshot.Sanitize(snapshot with { ImagePerceptualScore = double.NaN }).ImagePerceptualScore);
    }

    [Fact]
    public async Task One_server_lane_bounds_concurrent_metrics_and_waiting_cancellation_does_not_start_a_tool()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new ImagePerceptualQualityService("/metric", async (_, _, _, _) => {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await finish.Task;
            return new ToolProcessResult(0, Native, null);
        }, (_, _) => Task.FromResult(new string('a', 64)));
        var first = service.MeasureAsync("/source.png", "/candidate.webp", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var second = service.MeasureAsync("/source.png", "/candidate.webp", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, calls);
        finish.SetResult();
        Assert.NotNull((await first).Measurement);
    }

    [Fact]
    public async Task Observation_skips_unrequested_unhealthy_previews_and_never_falls_back_from_workers()
    {
        var calls = 0;
        var observer = new ImagePerceptualObservationService((_, _, _) => {
            calls++;
            return Task.FromResult(Report(90).ImagePerceptualQuality!);
        });
        Assert.Null(await observer.ObserveAsync(false, MediaKind.Image, true, false, false, "a", "b", CancellationToken.None));
        Assert.Null(await observer.ObserveAsync(true, MediaKind.Video, true, false, false, "a", "b", CancellationToken.None));
        Assert.Null(await observer.ObserveAsync(true, MediaKind.Image, true, true, false, "a", "b", CancellationToken.None));
        Assert.NotNull((await observer.ObserveAsync(true, MediaKind.Image, false, false, false, "a", "b", CancellationToken.None))!.Error);
        Assert.NotNull((await observer.ObserveAsync(true, MediaKind.Image, true, false, true, "a", "b", CancellationToken.None))!.Error);
        Assert.Equal(0, calls);
        Assert.NotNull((await observer.ObserveAsync(true, MediaKind.Image, true, false, false, "a", "b", CancellationToken.None))!.Measurement);
        Assert.Equal(1, calls);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.ObserveAsync(true, MediaKind.Image, true, false, false, "a", "b", cancel.Token));
    }

    private static VerificationReport Report(double score) => new([new("Decode", CheckOutcome.Passed, "Healthy")],
        ImagePerceptualQuality: new(new(96, 64, false, 0, score, [score]), null,
            new string('a', 64), new string('b', 64), new string('c', 64), 0.1));
}
