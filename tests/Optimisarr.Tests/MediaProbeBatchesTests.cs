using Optimisarr.Api.Library;

namespace Optimisarr.Tests;

public sealed class MediaProbeBatchesTests
{
    [Fact]
    public void Pending_batches_are_bounded_without_evicting_live_work()
    {
        var batches = new MediaProbeBatches();
        var accepted = Enumerable.Range(1, 20).Select(id => batches.Start([id])).ToArray();
        Assert.All(accepted, Assert.NotNull);
        Assert.Null(batches.Start([21]));
        Assert.All(accepted, batch => Assert.NotNull(batches.Get(batch!.Id)));
    }

    [Fact]
    public async Task Cancelling_a_queued_batch_immediately_marks_it_cancelled_without_processing_files()
    {
        var batches = new MediaProbeBatches();
        var view = batches.Start([1, 2])!;
        Assert.True(batches.Cancel(view.Id));
        Assert.Equal(MediaProbeBatchState.Cancelled, batches.Get(view.Id)!.State);
        var queued = await batches.TakeAsync(CancellationToken.None);
        Assert.False(queued!.TryStart());
        Assert.Equal(MediaProbeBatchState.Cancelled, queued.View().State);
        Assert.Empty(queued.View().Items);
    }
    [Fact]
    public async Task Cancelling_a_running_batch_preserves_the_current_file_outcome()
    {
        var batches = new MediaProbeBatches();
        var view = batches.Start([1, 2])!;
        var batch = (await batches.TakeAsync(CancellationToken.None))!;
        Assert.True(batch.TryStart());
        Assert.True(batches.Cancel(view.Id));
        Assert.True(batch.CancelRequested);
        Assert.Equal(MediaProbeBatchState.Running, batches.Get(view.Id)!.State);
        batch.Record(1, MediaProbeOutcome.Succeeded, null);
        batch.Finish(MediaProbeBatchState.Cancelled);
        Assert.Equal(1, batches.Get(view.Id)!.Succeeded);
        Assert.False(batches.Cancel(view.Id));
    }

    [Fact]
    public async Task Finished_batches_expire_on_reads_even_without_another_submission()
    {
        var clock = new Clock();
        var batches = new MediaProbeBatches(clock);
        var view = batches.Start([1])!;
        var batch = (await batches.TakeAsync(CancellationToken.None))!;
        Assert.True(batch.TryStart());
        batch.Finish(MediaProbeBatchState.Completed);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Null(batches.Get(view.Id));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
