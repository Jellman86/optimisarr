using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class WorkerSamplePreparationsTests
{
    private static readonly Job SampleJob = new() { Id = 7, MediaFileId = 3, Type = JobType.Preview };

    [Fact]
    public async Task A_slow_sample_does_not_hold_the_worker_request_and_is_offered_once_ready()
    {
        // Issue #428: hashing and cutting a 50 GB original inside the claim request outlived the
        // worker's 30-second request timeout, so the work was discarded and restarted forever.
        var source = new GatedSampleSource();
        using var preparations = new WorkerSamplePreparations(source, CancellationToken.None);

        var first = preparations.TryTake(SampleJob, 30, 60);
        Assert.Null(first.Sample);
        Assert.Null(first.Refusal);
        Assert.Equal(1, source.Started);

        var stillRunning = preparations.TryTake(SampleJob, 30, 60);
        Assert.Null(stillRunning.Sample);
        Assert.Equal(1, source.Started);

        source.Complete();
        await preparations.WhenSettledAsync(SampleJob, 30, 60);
        var ready = preparations.TryTake(SampleJob, 30, 60);
        Assert.Equal(source.Sample, ready.Sample);
    }

    [Fact]
    public async Task A_failed_preparation_is_reported_once_and_retried_on_the_next_request()
    {
        var source = new GatedSampleSource();
        using var preparations = new WorkerSamplePreparations(source, CancellationToken.None);

        _ = preparations.TryTake(SampleJob, 30, 0);
        source.Fail("The sample source is unavailable.");
        await preparations.WhenSettledAsync(SampleJob, 30, 0);

        Assert.Equal("The sample source is unavailable.", preparations.TryTake(SampleJob, 30, 0).Refusal);
        var retried = preparations.TryTake(SampleJob, 30, 0);
        Assert.Null(retried.Refusal);
        Assert.Equal(2, source.Started);
    }

    [Fact]
    public void Different_clip_windows_prepare_independently()
    {
        var source = new GatedSampleSource();
        using var preparations = new WorkerSamplePreparations(source, CancellationToken.None);

        _ = preparations.TryTake(SampleJob, 30, 0);
        _ = preparations.TryTake(SampleJob, 30, 120);

        Assert.Equal(2, source.Started);
    }

    private sealed class GatedSampleSource : IWorkerSampleSource
    {
        private TaskCompletionSource<PreparedWorkerSample> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
        public PreparedWorkerSample Sample { get; } = new("/work/.worker-source-x.mkv", 4,
            MediaProbeService.Parse("""{"streams":[],"format":{}}"""), 0);

        public Task<PreparedWorkerSample> PrepareAsync(Job job, int seconds, int start, CancellationToken token)
        {
            Interlocked.Increment(ref Started);
            var gate = _gate;
            if (gate.Task.IsCompleted) _gate = gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return gate.Task;
        }

        public void Complete() => _gate.SetResult(Sample);
        public void Fail(string reason) => _gate.SetException(new InvalidOperationException(reason));
    }
}
