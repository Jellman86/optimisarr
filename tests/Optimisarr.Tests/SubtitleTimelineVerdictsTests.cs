using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class SubtitleTimelineVerdictsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("optimisarr-subtitle-verdicts-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task An_unchanged_source_is_probed_once_across_jobs()
    {
        var source = Source("episode.mkv");
        var probe = new GatedProbe();
        var verdicts = new SubtitleTimelineVerdicts(probe.RunAsync, CancellationToken.None);
        probe.Complete(true);

        Assert.True(await verdicts.RequiresMatroskaAsync(source, [2, 3], CancellationToken.None));
        Assert.True(await verdicts.RequiresMatroskaAsync(source, [3, 2], CancellationToken.None));
        Assert.Equal(1, probe.Started);
    }

    [Fact]
    public async Task A_cancelled_claim_leaves_the_probe_running_for_the_next_claim()
    {
        // A 4K source's subtitle scan took 26 s inside a worker claim, past the worker's 30-second
        // request timeout. Cancelling the scan with the request meant every claim started it again.
        var source = Source("episode.mkv");
        var probe = new GatedProbe();
        var verdicts = new SubtitleTimelineVerdicts(probe.RunAsync, CancellationToken.None);
        using var timedOut = new CancellationTokenSource();

        var first = verdicts.RequiresMatroskaAsync(source, [2], timedOut.Token);
        await timedOut.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        probe.Complete(false);
        Assert.False(await verdicts.RequiresMatroskaAsync(source, [2], CancellationToken.None));
        Assert.Equal(1, probe.Started);
    }

    [Fact]
    public async Task A_changed_source_or_kept_track_set_is_probed_again()
    {
        var source = Source("episode.mkv");
        var probe = new GatedProbe();
        var verdicts = new SubtitleTimelineVerdicts(probe.RunAsync, CancellationToken.None);
        probe.Complete(false);

        await verdicts.RequiresMatroskaAsync(source, [2], CancellationToken.None);
        await verdicts.RequiresMatroskaAsync(source, [2, 3], CancellationToken.None);
        await File.AppendAllTextAsync(source, "more");
        await verdicts.RequiresMatroskaAsync(source, [2], CancellationToken.None);

        Assert.Equal(3, probe.Started);
    }

    [Fact]
    public async Task A_failed_probe_is_reported_and_tried_again()
    {
        var source = Source("episode.mkv");
        var probe = new GatedProbe();
        var verdicts = new SubtitleTimelineVerdicts(probe.RunAsync, CancellationToken.None);
        probe.Fail();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verdicts.RequiresMatroskaAsync(source, [2], CancellationToken.None));
        probe.Complete(true);
        Assert.True(await verdicts.RequiresMatroskaAsync(source, [2], CancellationToken.None));
        Assert.Equal(2, probe.Started);
    }

    private string Source(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "source");
        return path;
    }

    private sealed class GatedProbe
    {
        private TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }

        public Task<bool> RunAsync(string path, IReadOnlyCollection<int> kept, CancellationToken token)
        {
            Started++;
            return _result.Task.WaitAsync(token);
        }

        public void Complete(bool requiresMatroska)
        {
            if (_result.Task.IsCompleted) _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _result.SetResult(requiresMatroska);
        }

        public void Fail() =>
            _result.SetException(new InvalidOperationException("Source subtitle timing could not be probed safely before encoding."));
    }
}
