using System.Diagnostics;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// A job must end when its process does.
///
/// <para>Reading a redirected stream to the end waits for the last handle on the write end of the
/// pipe to close, which is not always the moment the process exits — a grandchild that inherited
/// the handle holds it open behind FFmpeg. The macOS sidecar lost a job for fifty-two minutes to
/// exactly this, with no FFmpeg running at all and the lease renewing perfectly underneath it, so
/// the same shape is defended against here rather than waited for.</para>
/// </summary>
public sealed class ChildOutputTests
{
    [Fact]
    public async Task What_the_child_wrote_still_comes_back()
    {
        // The ordinary case, and the one that must not regress: the stream has already finished by
        // the time the exit is noticed, and every byte of it is still returned.
        Assert.Equal(
            "Error while opening encoder",
            await ChildOutput.WithinGraceAsync(Task.FromResult("Error while opening encoder")));
    }

    [Fact]
    public async Task A_stream_that_never_ends_costs_the_grace_rather_than_the_job()
    {
        // Nothing ever completes this, which is precisely what a pipe held open by somebody else
        // looks like from here.
        var neverEnds = new TaskCompletionSource<string>().Task;

        var clock = Stopwatch.StartNew();
        var tail = await ChildOutput.WithinGraceAsync(neverEnds, ifItNeverEnds: "");
        clock.Stop();

        Assert.Equal("", tail);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), $"returned too early: {clock.Elapsed}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited too long: {clock.Elapsed}");
    }

    [Fact]
    public async Task A_read_cancelled_with_the_job_is_an_empty_tail_and_not_an_exception()
    {
        // The caller already holds the exit code, which is the part that decides anything. Letting
        // the tidying up throw would turn a finished job into a failed one.
        var cancelled = Task.FromCanceled<string>(new CancellationToken(canceled: true));

        Assert.Equal("", await ChildOutput.WithinGraceAsync(cancelled));
    }

    [Fact]
    public async Task A_read_that_faulted_on_a_closed_pipe_is_an_empty_tail()
    {
        var faulted = Task.FromException<string>(new IOException("the pipe has been ended"));

        Assert.Equal("", await ChildOutput.WithinGraceAsync(faulted));
    }
}
