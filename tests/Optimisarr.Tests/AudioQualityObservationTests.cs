using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class AudioQualityObservationTests
{
    [Fact]
    public async Task Off_video_and_preview_jobs_do_no_work()
    {
        var service = new AudioQualityObservationService((_, _, _) => throw new Exception("Must not run"));
        Assert.Null(await service.ObserveAsync(false, MediaKind.Audio, true, false, "source", "candidate", null, default));
        Assert.Null(await service.ObserveAsync(true, MediaKind.Video, true, false, "source", "candidate", null, default));
        Assert.Null(await service.ObserveAsync(true, MediaKind.Audio, true, true, "source", "candidate", null, default));
    }

    [Fact]
    public async Task Missing_worker_report_is_visible_without_local_media_reads()
    {
        var service = new AudioQualityObservationService((_, _, _) => throw new Exception("No fallback"));
        var remote = new RemoteVerificationEvidence(Guid.NewGuid(), new('a', 64), new('b', 64));
        var report = await service.ObserveAsync(true, MediaKind.Audio, true, false, "unread", "unread", remote, default);
        Assert.Equal("Worker", report!.MeasurementLocation);
        Assert.Contains("no audio quality report", report.UnavailableReason);
        Assert.Null(report.Evidence);
    }

    [Fact]
    public async Task Bad_decode_and_missing_tool_have_explicit_unavailable_reports()
    {
        var service = new AudioQualityObservationService(null);
        var report = await service.ObserveAsync(true, MediaKind.Audio, false, false, "unread", "unread", null, default);
        Assert.Contains("decode", report!.UnavailableReason);
        report = await service.ObserveAsync(true, MediaKind.Audio, true, false, "unread", "unread", null, default);
        Assert.Contains("tool", report!.UnavailableReason);
    }

    [Fact]
    public async Task Optional_tool_or_cleanup_failures_are_reports_rather_than_failed_gates()
    {
        var service = new AudioQualityObservationService((_, _, _) => throw new IOException("Scratch cleanup failed"));
        var report = await service.ObserveAsync(true, MediaKind.Audio, true, false, "source", "candidate", null, default);
        Assert.Null(report!.Evidence);
        Assert.Contains("Scratch cleanup failed", report.UnavailableReason);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_an_unavailable_score()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var service = new AudioQualityObservationService(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ObserveAsync(true, MediaKind.Audio,
            true, false, "unread", "unread", null, stop.Token));
    }
}
