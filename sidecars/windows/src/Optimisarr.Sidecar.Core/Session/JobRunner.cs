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
    Action<string>? report = null,
    Action<MonitorJob>? observe = null,
    Func<bool>? wantsPreview = null,
    Action<int, byte[]>? publishPreview = null,
    IFramePreviewExtractor? previewExtractor = null)
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
        var candidate = CandidatePath.For(candidatePrefix, assignment.OutputExtension);
        using var previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previews = wantsPreview is not null && publishPreview is not null
            ? new JobPreviewSampler(ffmpegPath, previewExtractor ?? new FfmpegFramePreviewExtractor(),
                wantsPreview, jpeg => publishPreview(assignment.JobId, jpeg))
            : null;

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

            if (previews is not null) _ = previews.TrySampleAsync(source, 0, previewLifetime.Token);

            // The per-title quality search, when the server sent one. It runs here, on the encoder
            // that will do the real encode, because a quality proven by measuring one encoder means
            // nothing on another. The server names every candidate; this machine measures them.
            var encodeArguments = assignment.Arguments;
            if (assignment.Search is { } firstStep)
            {
                var settled = await SearchAsync(
                    pairing, assignment, firstStep, scratch, source, cancellationToken);
                if (settled.Arguments is not { Count: > 0 } chosen)
                {
                    await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                    return new JobOutcome(assignment.JobId, false, settled.Reason ?? "The search chose no quality.");
                }

                encodeArguments = chosen;
            }

            // Checked before it is run, never after. The server decides what to encode; it does
            // not get to name files on this machine, which runs as LocalSystem. A command outside
            // the contract is refused whole and the job handed back — a repaired command is one
            // nobody wrote. See AssignmentCommand.
            if (AssignmentCommand.Refuse(encodeArguments, assignment.OutputExtension) is { } refused)
            {
                await client.ReleaseAsync(pairing, assignment.LeaseId, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false, $"The encode command was refused. {refused.Reason}");
            }

            report?.Invoke($"Job {assignment.JobId}: encoding with {assignment.VideoEncoder}");
            var arguments = AssignmentPlaceholders.Resolve(encodeArguments, source, candidatePrefix);

            var encoded = 0d;
            var result = await WhileRenewing(
                pairing, assignment, RemoteStage.Encoding, () => encoded, cancellationToken,
                token => transcoder.RunAsync(
                    ffmpegPath, arguments, new Progress<double>(seconds =>
                    {
                        encoded = seconds;
                        if (previews is not null) _ = previews.TrySampleAsync(source, seconds, token);
                    }), token, assignment.MaxCandidateBytes is { } maximum
                        ? new OutputSizeBudget(candidate, maximum) : null));

            if (result.SizeBudgetExceededAtBytes is { } observed)
            {
                await client.ReportSizeBudgetExceededAsync(
                    pairing, assignment.LeaseId, observed, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false,
                    $"Size saving: candidate exceeded the {assignment.MaxCandidateBytes:n0}-byte budget; the job was stopped.");
            }

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

            // The last mux write may happen after the monitor's final poll. Reject before hashing,
            // VMAF or upload, using the same lease-bound terminal outcome as an in-flight stop.
            var finalBytes = new FileInfo(candidate).Length;
            if (assignment.MaxCandidateBytes is { } finalMaximum && finalBytes > finalMaximum)
            {
                await client.ReportSizeBudgetExceededAsync(
                    pairing, assignment.LeaseId, finalBytes, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false,
                    $"Size saving: finished candidate exceeded the {finalMaximum:n0}-byte budget.");
            }
            if (assignment.MinCandidateBytes is { } finalMinimum && finalBytes < finalMinimum)
            {
                await client.ReportSizeBudgetUndershotAsync(
                    pairing, assignment.LeaseId, finalBytes, CancellationToken.None);
                return new JobOutcome(assignment.JobId, false,
                    $"Compression ceiling: finished candidate was below the {finalMinimum:n0}-byte floor.");
            }

            // The server's own measurement, run here and returned as the raw logs. Measuring is the
            // one part of verification a worker may contribute, and it is only an offer: if it
            // cannot be made the candidate is still delivered and the server measures for itself.
            // A candidate that encoded perfectly well must never be handed back because its score
            // could not be taken.
            //
            // Only when the server told us what the source hashes to. Evidence is bound to that
            // hash, so without it the measurement could never be believed and running one would be
            // a second decode of the whole file for a number certain to be thrown away.
            string? candidateHash = null;
            if (assignment.Quality.Measure
                && assignment.Quality.Commands.Count > 0
                && declaredHash is { Length: > 0 } boundTo)
            {
                var logs = await WhileRenewing(
                    pairing, assignment, RemoteStage.Measuring, null, cancellationToken,
                    token => MeasureCandidateForVerificationAsync(assignment, scratch, source, candidate, token));

                if (logs is { Count: > 0 })
                {
                    candidateHash = await JobTransfer.HashAsync(candidate, cancellationToken);
                    var offered = await client.ReportQualityAsync(
                        pairing, assignment.LeaseId, boundTo, candidateHash, logs, cancellationToken);
                    report?.Invoke(offered
                        ? $"Job {assignment.JobId}: quality evidence accepted, so the server need not measure"
                        : assignment.FullVerification is null
                            ? $"Job {assignment.JobId}: quality evidence was not accepted; the server will measure"
                            : $"Job {assignment.JobId}: quality evidence was not accepted; strict verification will fail this job");
                }
            }

            if (assignment.FullVerification is { } verification)
            {
                report?.Invoke($"Job {assignment.JobId}: full verification on this worker");
                candidateHash ??= await JobTransfer.HashAsync(candidate, cancellationToken);
                var evidence = await WhileRenewing(pairing, assignment, RemoteStage.Measuring, null,
                    cancellationToken, token => FullVerification.MeasureAsync(
                        ffmpegPath, source, candidate, verification, declaredHash ?? string.Empty, candidateHash, token));
                await client.ReportVerificationAsync(pairing, assignment.LeaseId, evidence, cancellationToken);
            }

            report?.Invoke($"Job {assignment.JobId}: delivering");
            await WhileRenewing(
                pairing, assignment, RemoteStage.Delivering, null, cancellationToken,
                async token =>
                {
                    await transfer.DeliverAsync(
                        pairing, assignment.LeaseId, candidate, declaredHash ?? string.Empty, null, token,
                        // Hashed already if the candidate was measured; the file can be tens of
                        // gigabytes and reading it twice buys nothing.
                        candidateHash);
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
            previewLifetime.Cancel();
            if (previews is not null) await previews.WaitForIdleAsync();
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
    /// <summary>What the search settled on, or why it did not.</summary>
    private readonly record struct SearchOutcome(IReadOnlyList<string>? Arguments, string? Reason)
    {
        public static SearchOutcome Failed(string reason) => new(null, reason);

        public static SearchOutcome Settled(IReadOnlyList<string> arguments) => new(arguments, null);
    }

    private async Task<SearchOutcome> SearchAsync(
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
                return SearchOutcome.Failed(
                    $"The search did not settle after {MaximumCandidates} candidates.");
            }

            report?.Invoke($"Job {assignment.JobId}: measuring quality {step.Quality}");

            var measured = await WhileRenewing(
                pairing, assignment, RemoteStage.Measuring, null, cancellationToken,
                token => MeasureCandidateAsync(step, assignment, scratch, source, token));
            if (!measured.Measured)
            {
                return SearchOutcome.Failed(measured.Reason!);
            }

            AdaptiveSearchDirection direction;
            try
            {
                direction = await client.ReportAdaptiveProbeAsync(
                    pairing, assignment.LeaseId, step.Quality,
                    measured.WindowBytes!, measured.Logs!, cancellationToken);
            }
            catch (SidecarException exception)
            {
                return SearchOutcome.Failed(
                    $"The measurement of quality {step.Quality} could not be reported: {exception.Message}");
            }

            if (direction.NextStep is { } next)
            {
                step = next;
                continue;
            }

            report?.Invoke(
                $"Job {assignment.JobId}: search chose quality {direction.SelectedQuality?.ToString() ?? "?"}");

            // The arguments that come back name the chosen quality. Without them there is nothing
            // safe to encode: the assignment's own were fixed before the search ran, so using them
            // would run the whole title at the library's value and discard the search.
            return direction.Arguments is { Count: > 0 } chosen
                ? SearchOutcome.Settled(chosen)
                : SearchOutcome.Failed(
                    "The server chose a quality but sent no command to encode it with.");
        }
    }

    /// <summary>
    /// Encodes one candidate's sample windows and scores each, returning the bytes and raw logs.
    ///
    /// <para>Null when any part could not be done. A partial answer is worse than none: the server
    /// pools the windows into a single score, so a missing window is a different measurement rather
    /// than a smaller one.</para>
    /// </summary>
    /// <summary>
    /// Scores the finished candidate against the source, with the server's own commands, and hands
    /// back the raw libvmaf logs.
    ///
    /// <para>Null when it could not be done. That is not a job failure — the caller delivers
    /// anyway and the server measures for itself — so the reasons are logged rather than returned.
    /// The scratch directory is deleted on every exit path, so a measurement that comes back wrong
    /// is undiagnosable afterwards unless the command, the shift applied and the score are written
    /// down as they happen.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> MeasureCandidateForVerificationAsync(
        Assignment assignment,
        string scratch,
        string source,
        string candidate,
        CancellationToken cancellationToken)
    {
        var commands = assignment.Quality.Commands;

        // A dropped frame can change alignment later in the file. Each sampled window must
        // measure its own shift against the pictures it will score.
        var frameSeconds = commands.Any(command => command.Any(argument =>
                argument.Contains(MeasurementPlaceholders.DistortedShift, StringComparison.Ordinal)))
            ? await ProbeFrameSecondsAsync(source, cancellationToken) ?? 1d / 25
            : (double?)null;

        var logs = new List<string>(commands.Count);
        for (var index = 0; index < commands.Count; index++)
        {
            string? distortedShift = null;
            if (commands[index].Any(argument =>
                argument.Contains(MeasurementPlaceholders.DistortedShift, StringComparison.Ordinal)))
            {
                distortedShift = await TimelineAlignment.MeasureAsync(
                    transcoder, ffmpegPath, commands[index], source, candidate, frameSeconds!.Value,
                    scratch, cancellationToken);
                if (distortedShift is null)
                {
                    report?.Invoke($"Job {assignment.JobId}: window {index} could not be aligned; "
                        + (assignment.FullVerification is null
                            ? "the server will score this itself"
                            : "strict verification will fail without server fallback"));
                    return null;
                }
            }

            report?.Invoke($"Job {assignment.JobId}: measuring window {index + 1}/{commands.Count}, "
                + $"distorted shift {distortedShift ?? "none"}");
            if (MeasurementCommand.Refuse(commands[index]) is { } refused)
            {
                report?.Invoke(
                    $"Job {assignment.JobId}: the command to measure window {index} was refused. {refused.Reason}");
                return null;
            }

            var log = Path.Combine(scratch, $"vmaf-{index}.json");
            var scored = await transcoder.RunAsync(
                ffmpegPath,
                MeasurementPlaceholders.Resolve(
                    commands[index], candidate, source, log, distortedShift),
                null,
                cancellationToken);

            if (!scored.Succeeded || !File.Exists(log))
            {
                report?.Invoke(
                    $"Job {assignment.JobId}: window {index} could not be measured "
                    + $"(FFmpeg exit {scored.ExitCode}): {scored.ErrorTail}");
                return null;
            }

            var contents = await File.ReadAllTextAsync(log, cancellationToken);
            if (contents.Length == 0)
            {
                report?.Invoke($"Job {assignment.JobId}: window {index} produced an empty libvmaf log");
                return null;
            }

            // Said per window, because a measurement that comes back wrong is otherwise
            // undiagnosable after the fact: the scratch directory goes on every exit path, so the
            // log this score came from exists nowhere once the job ends. A window full of
            // zero-scoring frames beside a respectable average is the signature of two timelines
            // misaligned rather than of a bad encode, and knowing which window is the diagnosis.
            if (VmafLogSummary.Of(contents) is { } summary)
            {
                report?.Invoke($"Job {assignment.JobId}: window {index} {summary}");
            }

            logs.Add(contents);
            TryDeleteFile(log);
        }

        return logs;
    }

    /// <summary>The video's start relative to its container, from ffprobe beside this FFmpeg.</summary>
    /// <summary>How long one picture of this file lasts, or null when ffprobe cannot say.</summary>
    private async Task<double?> ProbeFrameSecondsAsync(string file, CancellationToken cancellationToken)
    {
        var probe = FfprobeBeside(ffmpegPath);
        if (probe is null)
        {
            return null;
        }

        var result = await transcoder.ProbeAsync(
            probe, TimelineAlignment.FrameRateArguments(file), cancellationToken);
        return result.ExitCode == 0 ? TimelineAlignment.FrameSeconds(result.Output) : null;
    }


    /// <summary>
    /// ffprobe next to the FFmpeg this machine was told to use, or nothing.
    ///
    /// <para>Next to it rather than from the PATH: a sidecar with a bundled FFmpeg and a different
    /// FFmpeg on the PATH would otherwise probe with one and encode with the other, and the whole
    /// point of the lead is that it describes these two files as this FFmpeg reads them.</para>
    /// </summary>
    internal static string? FfprobeBeside(string ffmpeg)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(ffmpeg));
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var name = Path.GetFileName(ffmpeg);
        var probe = Path.Combine(
            directory,
            name.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase));
        return File.Exists(probe) ? probe : null;
    }

    /// <summary>
    /// What measuring one candidate produced, or why it produced nothing.
    ///
    /// <para><see cref="Reason"/> is the whole point. Every failure below used to return a bare
    /// null, which the search turned into "a sample encode or its measurement could not be
    /// completed" — five different problems wearing one sentence, and none of them naming the
    /// window, the quality, or a word of what FFmpeg said. PICARD handed back every job it was
    /// ever offered with exactly that line and nothing else to go on.</para>
    /// </summary>
    private readonly record struct CandidateMeasurement(
        IReadOnlyList<long>? WindowBytes, IReadOnlyList<string>? Logs, string? Reason)
    {
        public static CandidateMeasurement Failed(string reason) => new(null, null, reason);

        /// <summary>
        /// One byte count per sample window, in command order, so the server's size forecast can
        /// compare each with the source's own bytes over the same scenes.
        /// </summary>
        public static CandidateMeasurement Ok(IReadOnlyList<long> windowBytes, IReadOnlyList<string> logs) =>
            new(windowBytes, logs, null);

        public bool Measured => Reason is null;
    }

    private async Task<CandidateMeasurement> MeasureCandidateAsync(
        AdaptiveSearchStep step,
        Assignment assignment,
        string scratch,
        string source,
        CancellationToken cancellationToken)
    {
        if (step.SampleCommands.Count != step.Measurement.Commands.Count)
        {
            return CandidateMeasurement.Failed(
                $"The server sent {step.SampleCommands.Count} sample encode(s) and "
                + $"{step.Measurement.Commands.Count} command(s) to score them with.");
        }

        var windowBytes = new List<long>(step.SampleCommands.Count);
        var logs = new List<string>(step.SampleCommands.Count);

        for (var index = 0; index < step.SampleCommands.Count; index++)
        {
            var samplePrefix = Path.Combine(scratch, $"sample-q{step.Quality}-{index}");
            var sample = CandidatePath.For(samplePrefix, assignment.OutputExtension);
            var log = Path.Combine(scratch, $"sample-vmaf-q{step.Quality}-{index}.json");

            // A sample encode is the same contract as the real one — same placeholders, same
            // machine — and there are a dozen of them per job, so it is the larger surface of the
            // two rather than the smaller.
            if (AssignmentCommand.Refuse(step.SampleCommands[index], assignment.OutputExtension) is { } refused)
            {
                return CandidateMeasurement.Failed(
                    $"The sample encode for quality {step.Quality} was refused. {refused.Reason}");
            }

            var encode = await transcoder.RunAsync(
                ffmpegPath,
                AssignmentPlaceholders.Resolve(step.SampleCommands[index], source, samplePrefix),
                null,
                cancellationToken);
            if (!encode.Succeeded)
            {
                return CandidateMeasurement.Failed(
                    $"Sample {index + 1} at quality {step.Quality} would not encode "
                    + $"(FFmpeg exit {encode.ExitCode}): {encode.ErrorTail}");
            }

            if (!File.Exists(sample) || new FileInfo(sample).Length <= 0)
            {
                return CandidateMeasurement.Failed(
                    $"Sample {index + 1} at quality {step.Quality} encoded to nothing at all.");
            }

            windowBytes.Add(new FileInfo(sample).Length);

            // A sample begins at its own first picture, so there is no lead to remove — unlike a
            // finished candidate, where the measured window is a slice of a whole file.
            if (MeasurementCommand.Refuse(step.Measurement.Commands[index]) is { } refusedScore)
            {
                return CandidateMeasurement.Failed(
                    $"The command to score sample {index + 1} was refused. {refusedScore.Reason}");
            }

            var scored = await transcoder.RunAsync(
                ffmpegPath,
                MeasurementPlaceholders.Resolve(
                    step.Measurement.Commands[index], sample, source, log),
                null,
                cancellationToken);
            if (!scored.Succeeded)
            {
                return CandidateMeasurement.Failed(
                    $"Sample {index + 1} at quality {step.Quality} would not score "
                    + $"(FFmpeg exit {scored.ExitCode}): {scored.ErrorTail}");
            }

            if (!File.Exists(log))
            {
                return CandidateMeasurement.Failed(
                    $"Scoring sample {index + 1} at quality {step.Quality} wrote no libvmaf log.");
            }

            var sampleLog = await File.ReadAllTextAsync(log, cancellationToken);
            // The search is most of what a job spends its time on, and a sample window that scores
            // near zero is why a search settles somewhere absurd. Said here as well as for the
            // final measurement, so the two can be compared.
            if (VmafLogSummary.Of(sampleLog) is { } summary)
            {
                report?.Invoke(
                    $"Job {assignment.JobId}: quality {step.Quality} window {index} {summary}");
            }

            logs.Add(sampleLog);

            // Removed as they are measured. Four candidates across three windows is a dozen sample
            // encodes, and keeping them would need as much scratch again as the job itself.
            TryDeleteFile(sample);
            TryDeleteFile(log);
        }

        return CandidateMeasurement.Ok(windowBytes, logs);
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
        observe?.Invoke(new MonitorJob(assignment.JobId, assignment.Title, assignment.VideoEncoder, stage, encodedSeconds?.Invoke()));
        using var stageCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Half the window the server allows, bounded: often enough that a slow renewal still lands
        // before the lease lapses, rarely enough not to be chatter.
        var interval = TimeSpan.FromSeconds(Math.Clamp(assignment.RenewWithinSeconds / 2.0, 5, 15));

        // The lease is only really gone when the server says so, or when it has been out of touch
        // for longer than the window the lease was granted for. A single failed renewal used to
        // end the loop and take the job with it, so a restarted container — a deployment, which
        // happens often — threw away every encode that was running at the time, minutes in.
        var window = TimeSpan.FromSeconds(assignment.RenewWithinSeconds);
        var renewing = Task.Run(async () =>
        {
            var lastRenewed = DateTimeOffset.UtcNow;
            while (!stageCancelled.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stageCancelled.Token);
                    observe?.Invoke(new MonitorJob(assignment.JobId, assignment.Title, assignment.VideoEncoder, stage, encodedSeconds?.Invoke()));
                    await client.RenewAsync(
                        pairing, assignment.LeaseId, stage, encodedSeconds?.Invoke(), load(),
                        stageCancelled.Token);
                    lastRenewed = DateTimeOffset.UtcNow;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SidecarException problem) when (!problem.Recoverable)
                {
                    // The lease is no longer ours. Stop the work rather than finish an encode
                    // nobody will accept.
                    await stageCancelled.CancelAsync();
                    return;
                }
                catch (Exception)
                {
                    // Every other reason is the same reason: a renewal that did not happen. What
                    // stopped it says nothing about whether the lease survives; only how long it
                    // has now been since one landed does.
                }

                if (DateTimeOffset.UtcNow - lastRenewed >= window)
                {
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
