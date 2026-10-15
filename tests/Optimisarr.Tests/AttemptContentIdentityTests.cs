using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class AttemptContentIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "attempt identity " + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("source", false)]
    [InlineData("candidate", false)]
    [InlineData("source", true)]
    [InlineData("candidate", true)]
    public async Task Changed_or_removed_attempt_bytes_hold_work_without_penalising_the_media(string name, bool remove)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, "original attempt bytes");
        var hash = await FileContentIdentity.HashAsync(path, default);
        if (remove) File.Delete(path); else await File.WriteAllTextAsync(path, "new external bytes");
        var error = await Assert.ThrowsAsync<AttemptContentChangedException>(() =>
            FileContentIdentity.RequireMatchAsync(path, hash, name + " changed", default));
        Assert.True(OwnedPreparationFailure.IsDeferral(error));
        var media = new MediaFile { FailureCount = 2 };
        var job = new Job { Status = JobStatus.Verifying, ExecutionAttempt = 3, MediaFile = media,
            QualityRetryCount = 1, WorkOutputPath = path, PerceptualSearchJson = "saved search", VerificationReportJson = "saved rejection" };
        Assert.True(OwnedPreparationFailure.TryApply(job, 3, error.Message, DateTimeOffset.UtcNow));
        Assert.Equal(2, media.FailureCount);
        Assert.Equal(1, job.QualityRetryCount);
        Assert.Equal("saved search", job.PerceptualSearchJson);
        Assert.Equal("saved rejection", job.VerificationReportJson);
        Assert.Equal(path, job.WorkOutputPath);
        Assert.Contains(name, job.ErrorMessage);
        if (!remove) Assert.Equal("new external bytes", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task Missing_attempt_identity_cannot_authorise_measurement_or_become_a_media_failure(string? hash)
    {
        var error = await Assert.ThrowsAsync<AttemptContentChangedException>(() =>
            FileContentIdentity.RequireMatchAsync(Path.Combine(_root, "absent"), hash, "Source identity is missing.", default));
        Assert.True(OwnedPreparationFailure.IsDeferral(error));
    }

    [Fact]
    public async Task Unchanged_bytes_match_case_insensitively()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source");
        await File.WriteAllTextAsync(path, "original");
        await FileContentIdentity.RequireMatchAsync(path, (await FileContentIdentity.HashAsync(path, default)).ToUpperInvariant(), "unchanged", default);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Cancellation_is_not_relabelled_as_content_change_even_without_identity(string? hash)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileContentIdentity.RequireMatchAsync(Path.Combine(_root, "absent"), hash, "source", cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unavailable_attempt_guard_is_a_neutral_interruption(bool directory)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "unreadable");
        if (directory) Directory.CreateDirectory(path);
        var error = Assert.Throws<AttemptContentChangedException>(() => FileContentIdentity.OpenAttemptGuard(path, "Source unavailable."));
        Assert.True(OwnedPreparationFailure.IsDeferral(error));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
