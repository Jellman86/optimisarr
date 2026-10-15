using Optimisarr.Api.Queue;
using Optimisarr.Api.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class OwnedPreparationFailureTests
{
    [Fact]
    public void Ownership_and_reservation_deferrals_are_not_media_failures()
    {
        Assert.True(OwnedPreparationFailure.IsDeferral(new FileCleanupDeferredException()));
        Assert.True(OwnedPreparationFailure.IsDeferral(new DedupeReservationConflictException()));
        Assert.False(OwnedPreparationFailure.IsDeferral(new InvalidOperationException("decode failed")));
        Assert.False(OwnedPreparationFailure.IsDeferral(new IOException("source unreadable")));
        Assert.False(OwnedPreparationFailure.IsDeferral(new OperationCanceledException()));
    }

    [Fact]
    public void A_deferred_owned_preparation_preserves_evidence_and_does_not_spend_failure_or_retry_budgets()
    {
        var media = new MediaFile { FailureCount = 2 };
        var job = new Job { Status = JobStatus.Probing, ExecutionAttempt = 3, MediaFile = media,
            WorkOutputPath = "retained", PerceptualSearchJson = "search", VerificationReportJson = "report" };
        Assert.True(OwnedPreparationFailure.TryApply(job, 3, "reserved", DateTimeOffset.UtcNow));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(2, media.FailureCount);
        Assert.Equal(0, job.QualityRetryCount);
        Assert.Equal("retained", job.WorkOutputPath);
        Assert.Equal("search", job.PerceptualSearchJson);
        Assert.Equal("report", job.VerificationReportJson);
        Assert.Contains("reserved", job.ErrorMessage);
    }

    [Theory]
    [InlineData(JobStatus.Cancelled, 3)]
    [InlineData(JobStatus.Completed, 3)]
    [InlineData(JobStatus.Queued, 3)]
    [InlineData(JobStatus.Probing, 4)]
    public void Deferred_callbacks_cannot_change_cancelled_terminal_or_newer_work(JobStatus status, int attempt)
    {
        var job = new Job { Status = status, ExecutionAttempt = attempt, ErrorMessage = "current" };
        Assert.False(OwnedPreparationFailure.TryApply(job, 3, "old callback", DateTimeOffset.UtcNow));
        Assert.Equal(status, job.Status);
        Assert.Equal("current", job.ErrorMessage);
    }
}
