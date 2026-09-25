using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobClearingTests
{
    [Fact]
    public void A_new_attempt_clears_the_previous_attempts_failure_state()
    {
        var job = new Job
        {
            Id = 1,
            Status = JobStatus.Queued,
            Attempt = 1,
            ErrorMessage = "Verification failed: Perceptual quality (VMAF)",
            FailureCategory = FailureCategory.Verification,
            VideoEncoder = "old_encoder",
            RequestedVideoQuality = 22,
            EffectiveVideoQuality = 31,
            VideoQualityMode = "CQ",
            FfmpegArguments = "old command",
            VerificationPassed = false,
            VerificationReportJson = "{\"checks\":[]}",
            VerifiedAt = DateTimeOffset.UtcNow,
            OutputSizeBytes = 1234,
        };
        var now = DateTimeOffset.UtcNow;

        QueueDispatcher.PrepareForAttempt(job, now);

        Assert.Equal(JobStatus.Transcoding, job.Status);
        Assert.Equal(2, job.Attempt);
        Assert.Equal(now, job.StartedAt);
        Assert.Null(job.ErrorMessage);
        Assert.Null(job.FailureCategory);
        Assert.Null(job.VideoEncoder);
        Assert.Null(job.RequestedVideoQuality);
        Assert.Null(job.EffectiveVideoQuality);
        Assert.Null(job.VideoQualityMode);
        Assert.Null(job.FfmpegArguments);
        Assert.Null(job.VerificationPassed);
        Assert.Null(job.VerificationReportJson);
        Assert.Null(job.VerifiedAt);
        Assert.Null(job.OutputSizeBytes);
    }

    [Fact]
    public void A_rejected_remote_candidate_is_archived_before_its_active_result_is_cleared()
    {
        var verified = new DateTimeOffset(2026, 9, 20, 22, 20, 0, TimeSpan.Zero);
        var job = new Job
        {
            Status = JobStatus.Verifying,
            ExecutionAttempt = 1,
            StartedAt = verified.AddMinutes(-12),
            FinishedAt = verified,
            VideoEncoder = "hevc_videotoolbox",
            RequestedVideoQuality = 24,
            EffectiveVideoQuality = 40,
            VideoQualityMode = "CQ",
            FfmpegArguments = "-c:v hevc_videotoolbox",
            WorkOutputPath = "/work/rejected.mkv",
            OutputSizeBytes = 1234,
            VerificationPassed = false,
            VerificationReportJson = "{\"checks\":[{\"name\":\"Decode health\",\"outcome\":\"Failed\"}]}",
            VerifiedAt = verified,
            ProcessLog = "old encoder log"
        };

        JobAttemptHistory.RequeueAfterRejectedCandidate(
            job, "Scott's MacBook Air", "videotoolbox", verified.AddMinutes(1));

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.True(job.PreferSoftwareDecode);
        Assert.Null(job.VerificationPassed);
        Assert.Null(job.VerificationReportJson);
        Assert.Null(job.VerifiedAt);
        Assert.Null(job.OutputSizeBytes);
        Assert.Null(job.VideoEncoder);
        Assert.Null(job.RequestedVideoQuality);
        Assert.Null(job.EffectiveVideoQuality);
        Assert.Null(job.VideoQualityMode);
        Assert.Null(job.FfmpegArguments);
        Assert.Null(job.WorkOutputPath);
        Assert.Null(job.ProcessLog);
        Assert.Null(job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Equal("SoftwareDecode", job.RetryReason);

        var previous = Assert.Single(JobAttemptHistory.Read(job.AttemptHistoryJson));
        Assert.Equal(1, previous.Number);
        Assert.Equal("Scott's MacBook Air", previous.WorkerName);
        Assert.Equal("hevc_videotoolbox", previous.VideoEncoder);
        Assert.Equal("videotoolbox", previous.HardwareDecoder);
        Assert.Equal(false, previous.VerificationPassed);
        Assert.Equal(1234, previous.OutputSizeBytes);
        Assert.Contains("Decode health", previous.VerificationReportJson);
        Assert.Equal(verified, previous.VerifiedAt);
        Assert.Equal("-c:v hevc_videotoolbox", previous.FfmpegArguments);
        Assert.Equal("old encoder log", previous.ProcessLog);
    }

    [Fact]
    public void Starting_a_calibration_candidate_keeps_its_requested_quality()
    {
        var job = new Job { Type = JobType.Calibration, RequestedVideoQuality = 27 };

        QueueDispatcher.PrepareForAttempt(job, DateTimeOffset.UtcNow);

        Assert.Equal(27, job.RequestedVideoQuality);
    }

    private static readonly HashSet<int> NoLiveRollbacks = new();

    private static Job Job(int id, JobStatus status) => new() { Id = id, Status = status };

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void Terminal_jobs_without_a_live_rollback_are_clearable(JobStatus status)
    {
        Assert.True(JobClearing.IsClearable(Job(1, status), NoLiveRollbacks));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.Transcoding)]
    [InlineData(JobStatus.Verifying)]
    [InlineData(JobStatus.ReadyToReplace)]
    public void In_flight_jobs_are_never_clearable(JobStatus status)
    {
        Assert.False(JobClearing.IsClearable(Job(1, status), NoLiveRollbacks));
    }

    [Fact]
    public void A_completed_job_with_a_live_rollback_is_protected()
    {
        var liveRollbacks = new HashSet<int> { 7 };

        Assert.False(JobClearing.IsClearable(Job(7, JobStatus.Completed), liveRollbacks));
    }

    [Fact]
    public void A_completed_job_whose_rollback_is_spent_is_clearable()
    {
        // Job 7 has no entry in the live-rollback set (its replacement was rolled back or purged).
        var liveRollbacks = new HashSet<int> { 99 };

        Assert.True(JobClearing.IsClearable(Job(7, JobStatus.Completed), liveRollbacks));
    }
}
