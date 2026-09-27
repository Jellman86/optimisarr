using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

internal static class StudyRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = StudyOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(StudyOptions.Usage);
            return 2;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(options, stop.Token); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Study interrupted; partial results retained."); return 130; }
        catch (Exception ex) when (ex is IOException or ArgumentException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunAsync(StudyOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Output);
        // An interrupted run leaves this file unlocked. A simultaneous run must not rewrite its evidence.
        using var runLock = new FileStream(Path.Combine(options.Output, "study.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (options.ReportFrom is { } existing)
        {
            var measured = StudyRow.Parse(await File.ReadAllTextAsync(existing, cancellationToken));
            await File.WriteAllTextAsync(Path.Combine(options.Output, "report.md"), StudyReport.Markdown(measured, options), cancellationToken);
            // A CSV alone cannot prove that every requested source/quality/window was attempted.
            Console.WriteLine("Report regenerated; run completeness requires the original run.json.");
            return StudyIntegrity.Complete(measured, measured.Count) ? 0 : 1;
        }

        var rows = new List<StudyRow>();
        var errors = new List<string>();
        var sources = new List<object>();
        var expected = 0;
        var completed = false;
        var started = DateTimeOffset.UtcNow;
        object? toolchain = null;
        async Task SaveAsync()
        {
            await WriteAsync("scores.csv", StudyRow.Csv(rows));
            await WriteAsync("report.md", StudyReport.Markdown(rows, options));
            await WriteAsync("run.json", JsonSerializer.Serialize(new
            {
                schema = 1, started, updated = DateTimeOffset.UtcNow, completed,
                expectedMeasurements = expected, measured = rows.Count, errors, options, toolchain, sources,
                measurementPolicy = "v0-established-v1-ten-bit-encode-aware-cambi-cpu-v1"
            }, Json));
        }
        async Task WriteAsync(string name, string value)
        {
            var path = Path.Combine(options.Output, name);
            await File.WriteAllTextAsync(path + ".tmp", value, CancellationToken.None);
            File.Move(path + ".tmp", path, true);
        }

        try
        {
            await SaveAsync();
            var encoder = await FingerprintAsync(options.Ffmpeg, cancellationToken);
            var measurement = await FingerprintAsync(options.MeasurementFfmpeg, cancellationToken);
            var probeTool = await FingerprintAsync(options.Ffprobe, cancellationToken);
            toolchain = new { encoder, measurement, probeTool,
                studyAssembly = await HashAsync(typeof(StudyRunner).Assembly.Location, cancellationToken),
                coreAssembly = await HashAsync(typeof(QualityScoreService).Assembly.Location, cancellationToken) };
            var probe = new MediaProbeService(probeTool.Path);
            var quality = new QualityScoreService(measurement.Path);
            var clips = Path.Combine(options.Output, "clips");
            Directory.CreateDirectory(clips);
            foreach (var source in options.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var media = await probe.ProbeAsync(source, cancellationToken);
                var duration = media.VideoDurationSeconds ?? media.DurationSeconds ?? 0;
                var windows = VmafWindowPlanner.PlanAdaptive(duration);
                var depth = PixelFormatInfo.Parse(media.PixelFormat, media.BitsPerRawSample)?.BitDepth;
                if (!media.Success || media.Width is not > 0 || media.Height is not > 0 || windows.Count == 0
                    || media.IsHdr || depth is not (8 or 10) || media.VideoFrameRate is not > 0 or >= 45)
                {
                    errors.Add($"{source}: requires a valid 8/10-bit SDR source below 45 fps with known duration and frame rate. {media.Error}");
                    await SaveAsync();
                    continue;
                }
                var sourceHash = await HashAsync(source, cancellationToken);
                // The full digest distinguishes identical basenames and is stable across endpoint paths.
                var sourceId = $"{Path.GetFileName(source)} [{sourceHash}]";
                sources.Add(new { path = Path.GetFullPath(source), sha256 = sourceHash, media, windows });
                expected += windows.Count * options.Qualities.Count * 2;
                var baseline = media.Width >= 3840 || media.Height >= 2160 ? StudyOptions.BaselineUhd : StudyOptions.BaselineHd;
                var candidate = options.CandidateModelFor(media.Width.Value, media.Height.Value);
                if (StudyIntegrity.IsBaseline(candidate)) throw new ArgumentException("The candidate must differ from both baseline models.");
                var lead = media.VideoStartSeconds is { } video && media.ContainerStartSeconds is { } container ? video - container : (double?)null;
                var context = new QualityMeasurementContext(media.Width.Value, media.Height.Value, false, false,
                    ReferenceDurationSeconds: duration, ReferenceFrameRate: media.VideoFrameRate, ReferenceContainerLeadSeconds: lead);
                // Load both models with the exact graph/bit depth before spending time encoding a ladder.
                foreach (var model in new[] { baseline, candidate })
                {
                    var preflight = await quality.MeasureAsync(source, source, context with
                    {
                        ReferenceStartSeconds = windows[0].StartSeconds, DistortedStartSeconds = windows[0].StartSeconds,
                        MeasureDurationSeconds = 2, ModelVersion = model,
                        EncodedVideo = new(media.Width.Value, media.Height.Value, depth.Value)
                    }, cancellationToken);
                    if (!preflight.Measured) throw new InvalidOperationException($"Model preflight failed ({model}): {preflight.Error}");
                }

                foreach (var crf in options.Qualities)
                {
                    for (var index = 0; index < windows.Count; index++)
                    {
                        var window = windows[index];
                        var spec = new TranscodeSpec(source, "{{clip}}", options.Codec, crf, options.Preset, TonemapToSdr: false,
                            ClipStartSeconds: window.StartSeconds, ClipSeconds: window.DurationSeconds, VideoOnly: true)
                        { SourceBitDepth = depth };
                        var argv = FfmpegCommandBuilder.Build(spec, 0, options.Encoder);
                        // Content identity replaces the host-specific source path in the cache key.
                        var key = StudyIntegrity.ClipKey(sourceHash, encoder.Sha256 + "\n" + encoder.Version,
                            argv.Select(a => a == source ? "{{source}}" : a).ToList());
                        var clip = Path.Combine(clips, key + ".mkv");
                        await EnsureClipAsync(encoder.Path, argv, clip, cancellationToken);
                        var encoded = await probe.ProbeAsync(clip, cancellationToken);
                        var encodedDepth = PixelFormatInfo.Parse(encoded.PixelFormat, encoded.BitsPerRawSample)?.BitDepth;
                        if (!encoded.Success || encoded.Width is not > 0 || encoded.Height is not > 0 || encodedDepth is not (8 or 10))
                            throw new InvalidOperationException($"Cannot confirm encoded dimensions and bit depth for {clip}.");
                        var clipHash = await HashAsync(clip, cancellationToken);
                        await File.WriteAllTextAsync(clip + ".json", JsonSerializer.Serialize(new
                        { key, sourceHash, encoder = encoder.Sha256, argv, clipHash, encoded }, Json), cancellationToken);
                        foreach (var model in new[] { baseline, candidate })
                        {
                            var result = await quality.MeasureAsync(source, clip, context with
                            {
                                ReferenceStartSeconds = window.StartSeconds, MeasureDurationSeconds = window.DurationSeconds,
                                DistortedIsCutClip = true, ModelVersion = model, DistortedShiftToken = "0",
                                EncodedVideo = new(encoded.Width.Value, encoded.Height.Value, encodedDepth.Value)
                            }, cancellationToken);
                            var scores = result.Scores;
                            rows.Add(new StudyRow(sourceId, index, options.Encoder, crf, new FileInfo(clip).Length, model,
                                scores?.VmafHarmonicMean, scores?.VmafFifthPercentile, scores?.VmafMin,
                                scores?.VmafMean, scores?.FrameCount, result.Error, clipHash));
                            Console.WriteLine($"{Path.GetFileName(source)} q{crf} w{index} {model}: "
                                + (scores is null ? $"FAILED: {result.Error}" : $"harmonic {scores.VmafHarmonicMean:0.00}, p5 {scores.VmafFifthPercentile:0.00}, min {scores.VmafMin:0.00}"));
                            await SaveAsync();
                        }
                    }
                }
                if (await HashAsync(source, cancellationToken) != sourceHash)
                    throw new IOException($"Source changed during measurement: {source}");
            }
            completed = errors.Count == 0 && StudyIntegrity.Complete(rows, expected);
            if (!completed) errors.Add("Study incomplete: missing, failed, duplicate or mismatched measurements; no gate change is justified.");
            return completed ? 0 : 1;
        }
        catch (Exception ex)
        {
            errors.Add(ex is OperationCanceledException ? "Interrupted" : ex.Message);
            throw;
        }
        finally { await SaveAsync(); }
    }

    internal static async Task EnsureClipAsync(string executable, IReadOnlyList<string> argv, string clip, CancellationToken token)
    {
        var checksum = clip + ".sha256";
        if (File.Exists(clip) && new FileInfo(clip).Length > 0 && File.Exists(checksum)
            && await File.ReadAllTextAsync(checksum, token) == await HashAsync(clip, token)) return;
        var partial = clip + ".partial.mkv";
        try
        {
            await RunProcessAsync(executable, argv.Select(a => a == "{{clip}}" ? partial : a).ToList(), token);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0) throw new IOException("Encoder produced no clip.");
            var hash = await HashAsync(partial, token);
            File.Move(partial, clip, true);
            await File.WriteAllTextAsync(checksum, hash, token);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private sealed record ToolFingerprint(string Path, string Sha256, string Version);

    private static async Task<ToolFingerprint> FingerprintAsync(string executable, CancellationToken token)
    {
        var path = executable;
        if (!File.Exists(path))
        {
            path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => Path.Combine(p, executable + (OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "")))
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Executable not found: {executable}");
        }
        path = Path.GetFullPath(path);
        return new(path, await HashAsync(path, token), await RunProcessAsync(path, ["-version"], token));
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }

    internal static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> argv, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in argv) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {(await stderr)[..Math.Min((await stderr).Length, 4000)]}");
            return await stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            if (!token.IsCancellationRequested) throw new InvalidOperationException("Study process exceeded its 30-minute limit.");
            throw;
        }
    }
}
