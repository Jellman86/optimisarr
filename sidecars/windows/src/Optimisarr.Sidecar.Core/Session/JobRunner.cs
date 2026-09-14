namespace Optimisarr.Sidecar.Core.Session;

/// <summary>How a job ended, in the terms the operator and the server both care about.</summary>
public sealed record JobOutcome(int JobId, bool Delivered, string Detail);

/// <summary>
/// One job, from claim to delivery.
///
/// <para>Every long stage runs beside the lease renewal, and whichever finishes first stops the
/// other. A completed stage no longer needs renewing; a refused renewal cancels the transfer or the
/// encode immediately, so this machine does no more work under a lease the server has taken back.
/// Without that, a worker whose lease lapsed would carry on for an hour and deliver something the
/// server had already reassigned.</para>
/// </summary>
public sealed class JobRunner(
    SidecarClient client,
    JobTransfer transfer,
    ITranscoder transcoder,
    string ffmpegPath,
    string scratchRoot,
    Func<MachineLoad?> load,
    Action<string>? report = null)
{
    public async Task<JobOutcome> RunAsync(
        StoredPairing pairing, Assignment assignment, CancellationToken cancellationToken)
    {
        // Its own directory, named for the job, so two jobs cannot tread on each other and a
        // leftover from a crash is obvious rather than mysterious.
        var scratch = Path.Combine(scratchRoot, $"job-{assignment.JobId}");
        Directory.CreateDirectory(scratch);

        var source = Path.Combine(scratch, "source");
        var candidatePrefix = Path.Combine(scratch, "candidate");
        var candidate = candidatePrefix + assignment.OutputExtension;

        try
        {
            report?.Invoke($"Job {assignment.JobId}: fetching {assignment.Title}");
            var declaredHash = await WhileRenewing(
                pairing, assignment, RemoteStage.FetchingSource, null, cancellationToken,
                token => transfer.FetchSourceAsync(pairing, assignment.LeaseId, source, null, token));

            // Verified before a single frame is encoded. A corrupted source would otherwise cost a
            // full encode before the server rejected the result for the wrong source hash.
            if (declaredHash is { Length: > 0 })
            {
                var actual = await JobTransfer.HashAsync(source, cancellationToken);
                if (!string.Equals(actual, declaredHash, StringComparison.OrdinalIgnoreCase))
                {
                    await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                    return new JobOutcome(assignment.JobId, false,
                        "The source did not arrive intact (hash mismatch), so it was not encoded.");
                }
            }

            report?.Invoke($"Job {assignment.JobId}: encoding with {assignment.VideoEncoder}");
            var arguments = AssignmentPlaceholders.Resolve(assignment.Arguments, source, candidatePrefix);

            var encoded = 0d;
            var result = await WhileRenewing(
                pairing, assignment, RemoteStage.Encoding, () => encoded, cancellationToken,
                token => transcoder.RunAsync(
                    ffmpegPath, arguments, new Progress<double>(seconds => encoded = seconds), token));

            if (!result.Succeeded)
            {
                await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false,
                    $"FFmpeg exited with code {result.ExitCode}. {FirstLine(result.ErrorTail)}");
            }

            if (!File.Exists(candidate))
            {
                await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false,
                    "FFmpeg reported success but produced no candidate file.");
            }

            report?.Invoke($"Job {assignment.JobId}: delivering");
            await WhileRenewing(
                pairing, assignment, RemoteStage.Delivering, null, cancellationToken,
                async token =>
                {
                    await transfer.DeliverAsync(
                        pairing, assignment.LeaseId, candidate, declaredHash ?? string.Empty, null, token);
                    return true;
                });

            return new JobOutcome(assignment.JobId, true, "Delivered");
        }
        catch (SidecarException exception)
        {
            // The lease is gone, or the server refused the result. Giving it back is best effort:
            // if the lease has lapsed the server has already reclaimed it anyway.
            await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
            return new JobOutcome(assignment.JobId, false, exception.Message);
        }
        finally
        {
            // Never left behind. A worker that kept every source it was ever sent would fill a disk
            // in a weekend, and nothing here is of any use once the job has ended.
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// Runs one stage while keeping the lease alive, and stops the stage the moment the lease is
    /// refused.
    /// </summary>
    private async Task<T> WhileRenewing<T>(
        StoredPairing pairing,
        Assignment assignment,
        RemoteStage stage,
        Func<double>? encodedSeconds,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> work)
    {
        using var stageCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Half the window the server allows, bounded: often enough that a slow renewal still lands
        // before the lease lapses, rarely enough not to be chatter.
        var interval = TimeSpan.FromSeconds(Math.Clamp(assignment.RenewWithinSeconds / 2.0, 5, 15));

        var renewing = Task.Run(async () =>
        {
            while (!stageCancelled.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stageCancelled.Token);
                    await client.RenewAsync(
                        pairing, assignment.LeaseId, stage, encodedSeconds?.Invoke(), load(),
                        stageCancelled.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SidecarException)
                {
                    // The lease is no longer ours. Stop the work rather than finish an encode
                    // nobody will accept.
                    await stageCancelled.CancelAsync();
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            return await work(stageCancelled.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SidecarException(
                "The lease lapsed while this stage was running, so the work was stopped.",
                recoverable: false);
        }
        finally
        {
            await stageCancelled.CancelAsync();
            await renewing;
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file still held open by a process that has not quite exited. The next run's sweep
            // will get it; failing the job over tidying would be absurd.
        }
    }
}
