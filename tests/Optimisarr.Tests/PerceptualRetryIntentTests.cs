using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class PerceptualRetryIntentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private static PerceptualSearchSummary Summary() => new(MediaKind.Image, "libwebp", new('a', 64), 85, null, [],
        new([new(80, 1000, new(true, true, 90, null)), new(60, 800, new(true, true, 86, null))],
            new(null, 60, false, "measured")), RetryValue: 70);
    private static Job Job(PerceptualSearchSummary? summary = null) => new()
        { RetryReason = "HigherPerceptualQualityRetry", QualityRetryCount = 1, PerceptualSearchJson = JsonSerializer.Serialize(summary ?? Summary(), Json) };

    [Fact]
    public void Fresh_retry_attempt_retains_its_frozen_intent_but_clears_the_last_attempts_selected_fields()
    {
        var job = Job();
        job.AdaptiveImageQuality = 60;
        QueueDispatcher.PrepareForAttempt(job, DateTimeOffset.UtcNow);
        Assert.Null(job.AdaptiveImageQuality);
        Assert.Equal(70, QueueDispatcher.ReadPerceptualRetry(job)!.RetryValue);
        Assert.Equal(new string('a', 64), QueueDispatcher.ReadPerceptualRetry(job)!.SourceSha256);
    }

    [Fact]
    public void Manual_or_unattributed_retries_cannot_adopt_automatic_retry_intent()
    {
        var job = Job();
        job.RetryReason = "ManualRetry";
        Assert.Null(QueueDispatcher.ReadPerceptualRetry(job));
        QueueDispatcher.PrepareForAttempt(job, DateTimeOffset.UtcNow);
        Assert.Null(job.PerceptualSearchJson);
    }

    [Fact]
    public void Malformed_or_out_of_contract_intents_are_refused()
    {
        foreach (var summary in new[] { Summary() with { RetryValue = 50 }, Summary() with { RetryValue = 101 },
            Summary() with { Encoder = "unknown" }, Summary() with { SourceSha256 = "invalid" } })
            Assert.Null(QueueDispatcher.ReadPerceptualRetry(Job(summary)));
        var job = Job();
        job.PerceptualSearchJson = "{broken";
        Assert.Null(QueueDispatcher.ReadPerceptualRetry(job));
        job.PerceptualSearchJson = "{\"retryValue\":70,\"result\":{\"probes\":[{\"value\":80}],\"decision\":null}}";
        Assert.Null(QueueDispatcher.ReadPerceptualRetry(job));
    }
}
