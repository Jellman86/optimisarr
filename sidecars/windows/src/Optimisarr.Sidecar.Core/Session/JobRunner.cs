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

            // The per-title quality search, when the server sent one. It runs here, on the encoder
            // that will do the real encode, because a quality proven by measuring one encoder means
            // nothing on another. The server names every candidate; this machine measures them.
            var encodeArguments = assignment.Arguments;
            if (assignment.Search is { } firstStep)
            {
                var settled = await SearchAsync(
                    pairing, assignment, firstStep, scratch, source, cancellationToken);
                if (settled is null)
                {
                    await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                    return new JobOutcome(assignment.JobId, false,
                        "A sample encode or its measurement could not be completed, so no quality was chosen.");
                }

                encodeArguments = settled;
            }

            report?.Invoke($"Job {assignment.JobId}: encoding with {assignment.VideoEncoder}");
            var arguments = AssignmentPlaceholders.Resolve(encodeArguments, source, candidatePrefix);

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
    /// Measures each candidate the server asks for, until it stops asking, and returns the encode
    /// it settled on.
    ///
    /// <para>Bounded by the server, which sends at most four candidates and then names one. Null
    /// when a measurement could not be made: the job goes back rather than being encoded at a
    /// quality nobody chose. Falling through to the assignment's own arguments would run the whole
    /// title at the library's baseline, discard everything the search measured, and look like a
    /// perfectly successful job — the worst kind of wrong.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> SearchAsync(
        StoredPairing pairing,
        Assignment assignment,
        AdaptiveSearchStep first,
        string scratch,
        string source,
        CancellationToken cancellationToken)
    {
        var step = first;

        // The server bounds its own search at four candidates, but this machine should not depend
        // on that to stop: a bound only the other end enforces is not a bound. Twice the expected
        // number leaves ordinary searches untouched and still ends a conversation that has stopped
        // making sense.
        const int MaximumCandidates = 8;

        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= MaximumCandidates)
            {
                report?.Invoke(
                    $"Job {assignment.JobId}: the search did not settle after {MaximumCandidates} candidates");
                return null;
            }

            report?.Invoke($"Job {assignment.JobId}: measuring quality {step.Quality}");

            var measured = await WhileRenewing(
                pairing, assignment, RemoteStage.Measuring, null, cancellationToken,
                token => MeasureCandidateAsync(step, assignment, scratch, source, token));
            if (measured is null)
            {
                return null;
            }

            AdaptiveSearchDirection direction;
            try
            {
                direction = await client.ReportAdaptiveProbeAsync(
                    pairing, assignment.LeaseId, step.Quality,
                    measured.Value.Bytes, measured.Value.Logs, cancellationToken);
            }
            catch (SidecarException)
            {
                return null;
            }

            if (direction.NextStep is { } next)
            {
                step = next;
                continue;
            }

            report?.Invoke(
                $"Job {assignment.JobId}: search chose quality {direction.SelectedQuality?.ToString() ?? "?"}");

            // The arguments that come back name the chosen quality. Without them there is nothing
            // safe to encode: the assignment's own were fixed before the search ran.
            return direction.Arguments is { Count: > 0 } ? direction.Arguments : null;
        }
    }

    /// <summary>
    /// Encodes one candidate's sample windows and scores each, returning the bytes and raw logs.
    ///
    /// <para>Null when any part could not be done. A partial answer is worse than none: the server
    /// pools the windows into a single score, so a missing window is a different measurement rather
    /// than a smaller one.</para>
    /// </summary>
    private async Task<(long Bytes, IReadOnlyList<string> Logs)?> MeasureCandidateAsync(
        AdaptiveSearchStep step,
        Assignment assignment,
        string scratch,
        string source,
        CancellationToken cancellationToken)
    {
        if (step.SampleCommands.Count != step.Measurement.Commands.Count)
        {
            return null;
        }

        long bytes = 0;
        var logs = new List<string>(step.SampleCommands.Count);

        for (var index = 0; index < step.SampleCommands.Count; index++)
        {
            var samplePrefix = Path.Combine(scratch, $"sample-q{step.Quality}-{index}");
            var sample = samplePrefix + assignment.OutputExtension;
            var log = Path.Combine(scratch, $"sample-vmaf-q{step.Quality}-{index}.json");

            var encode = await transcoder.RunAsync(
                ffmpegPath,
                AssignmentPlaceholders.Resolve(step.SampleCommands[index], source, samplePrefix),
                null,
                cancellationToken);
            if (!encode.Succeeded || !File.Exists(sample) || new FileInfo(sample).Length <= 0)
            {
                return null;
            }

            bytes += new FileInfo(sample).Length;

            // A sample begins at its own first picture, so there is no lead to remove — unlike a
            // finished candidate, where the measured window is a slice of a whole file.
            var scored = await transcoder.RunAsync(
                ffmpegPath,
                MeasurementPlaceholders.Resolve(
                    step.Measurement.Commands[index], sample, source, log),
                null,
                cancellationToken);
            if (!scored.Succeeded || !File.Exists(log))
            {
                return null;
            }

            logs.Add(await File.ReadAllTextAsync(log, cancellationToken));

            // Removed as they are measured. Four candidates across three windows is a dozen sample
            // encodes, and keeping them would need as much scratch again as the job itself.
            TryDeleteFile(sample);
            TryDeleteFile(log);
        }

        return (bytes, logs);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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
