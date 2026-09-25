namespace Optimisarr.Sidecar.Core;

/// <summary>
/// Reading what a child process wrote, once it has stopped being a child process.
///
/// <para>Reading a redirected stream to the end means waiting for end-of-file, and end-of-file
/// does not arrive when the process exits — it arrives when the last handle to the write end of
/// the pipe closes. Those are usually the same moment and occasionally are not: a grandchild that
/// inherited the handle, or another process that picked it up, holds the pipe open behind FFmpeg
/// and the read waits for as long as that unrelated process happens to live.</para>
///
/// <para>Nothing recovers from it, which is what makes it worth defending against rather than
/// tolerating. The lease renewal loop keeps renewing, so the server sees a healthy worker making
/// progress and leaves the job alone; on the macOS sidecar this held a job for fifty-two minutes
/// with no FFmpeg running at all, and would have held it until the app was killed.</para>
///
/// <para>So the exit is the authority and the pipe is given a moment, not a veto. The stream is
/// still read in full in every ordinary case — it has already finished by the time the exit is
/// observed — and a stream that will not end costs a couple of seconds rather than the job.</para>
/// </summary>
public static class ChildOutput
{
    /// <summary>How long a stream is given to end after its process already has.</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>Gives a progress-stream reader the same bounded drain as text output.</summary>
    public static async Task WithinGraceAsync(Task reading)
    {
        if (await Task.WhenAny(reading, Task.Delay(DrainGrace)).ConfigureAwait(false) == reading)
        {
            try { await reading.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return;
        }

        _ = reading.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for <paramref name="reading"/> to finish, for no longer than the grace.
    /// </summary>
    /// <param name="reading">A read of a redirected stream, already in flight.</param>
    /// <param name="ifItNeverEnds">What to return when the stream does not reach its end in time.</param>
    public static async Task<string> WithinGraceAsync(
        Task<string> reading, string ifItNeverEnds = "")
    {
        if (reading.IsCompleted)
        {
            return await Read(reading, ifItNeverEnds);
        }

        // Not linked to the caller's token: this runs after the process has exited, and a
        // cancelled job still wants its answer rather than an exception from the tidying up.
        var finished = await Task.WhenAny(reading, Task.Delay(DrainGrace)).ConfigureAwait(false);
        if (finished == reading)
        {
            return await Read(reading, ifItNeverEnds);
        }

        // Abandoned, not forgotten. Disposing the process closes the stream and the read then ends
        // one way or the other; this is only so its result is looked at rather than left unobserved.
        _ = reading.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return ifItNeverEnds;

        static async Task<string> Read(Task<string> reading, string ifItNeverEnds)
        {
            // A read that faulted or was cancelled with the job says nothing about the process's
            // exit code, which the caller already has and is the thing that matters.
            try { return await reading.ConfigureAwait(false); }
            catch (OperationCanceledException) { return ifItNeverEnds; }
            catch (IOException) { return ifItNeverEnds; }
            catch (ObjectDisposedException) { return ifItNeverEnds; }
        }
    }
}
