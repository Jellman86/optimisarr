using System.Net;
using System.Text;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The fake server and fake encoder both job-runner suites drive.
///
/// <para>Shared rather than copied. Two fakes of one protocol drift, and a suite passing against a
/// fake that no longer matches the other is worth nothing — which is the shape of most of what has
/// gone wrong in this sidecar.</para>
/// </summary>
internal sealed class FakeWorkerServer(byte[] source, string sourceHash) : HttpMessageHandler
{
    public readonly List<string> Calls = [];
    public byte[] Delivered = [];
    public bool Completed;
    /// <summary>The evidence body a worker offered, if it offered any.</summary>
    public string? QualityBody;
    /// <summary>Whether the candidate had already arrived when the evidence was offered.</summary>
    public bool QualityOfferedBeforeDelivery;
    /// <summary>What the server answers an offer of evidence with.</summary>
    public HttpStatusCode QualityStatus = HttpStatusCode.OK;
    /// <summary>Whether the source response carries the hash header. An older server sends none.</summary>
    public bool DeclareSourceHash = true;
    /// <summary>What the server answers a lease renewal with. 503 is a restarting container.</summary>
    public HttpStatusCode RenewStatus = HttpStatusCode.OK;
    /// <summary>Answer this many delivery-related calls with 502 before behaving, as a restart does.</summary>
    public int DeliveryFailuresRemaining;
    /// <summary>How many delivery-related calls were refused that way.</summary>
    public int DeliveryRefusals;
    /// <summary>How many renewals were asked for.</summary>
    public int RenewCalls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add($"{request.Method} {path}");

        if (path.EndsWith("/renew", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref RenewCalls);
            return Task.FromResult(new HttpResponseMessage(RenewStatus));
        }

        if (path.EndsWith("/source", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(source),
            };
            if (DeclareSourceHash)
            {
                response.Headers.Add("X-Optimisarr-Source-Sha256", sourceHash);
            }
            return Task.FromResult(response);
        }

        if (path.EndsWith("/quality", StringComparison.Ordinal))
        {
            QualityBody = request.Content!.ReadAsStringAsync(cancellationToken).Result;
            QualityOfferedBeforeDelivery = Delivered.Length == 0;
            return Task.FromResult(new HttpResponseMessage(QualityStatus)
            {
                Content = new StringContent(
                    """{"leaseId":"x","vmafHarmonicMean":95.0}""", Encoding.UTF8, "application/json"),
            });
        }

        if (path.EndsWith("/result/offset", StringComparison.Ordinal))
        {
            if (Blink()) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)); }
            return Json($$"""{"bytes":{{Delivered.Length}}}""");
        }

        if (path.EndsWith("/result/complete", StringComparison.Ordinal))
        {
            if (Blink()) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)); }
            Completed = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        if (path.EndsWith("/result", StringComparison.Ordinal))
        {
            if (Blink()) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)); }
            var body = request.Content!.ReadAsByteArrayAsync(cancellationToken).Result;
            Delivered = [.. Delivered, .. body];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    /// <summary>A restarting container, one refusal at a time.</summary>
    private bool Blink()
    {
        if (DeliveryFailuresRemaining <= 0) { return false; }
        DeliveryFailuresRemaining--;
        DeliveryRefusals++;
        return true;
    }

    private static Task<HttpResponseMessage> Json(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
}

/// <summary>
/// FFmpeg, faked.
///
/// <para>It writes the file the command told it to write, taken from the last argument, rather than
/// one the test named. That is not fussiness: the runner rebuilds the candidate's path from the
/// assignment's extension, and a fake writing wherever the test pointed it meant the two could
/// disagree for as long as they liked with every test still green. They did — FFmpeg wrote
/// candidate.mkv and the sidecar looked for candidatemkv, on every job, for a day.</para>
/// </summary>
internal sealed class FakeMeasuringTranscoder(
    int exitCode = 0, string? writeCandidate = null, string errorTail = "") : ITranscoder
{
    public IReadOnlyList<string>? Arguments { get; private set; }
    public List<IReadOnlyList<string>> AllRuns { get; } = [];
    public List<IReadOnlyList<string>> Probes { get; } = [];

    /// <summary>Written by any run whose arguments name a libvmaf log, so a measurement can succeed.</summary>
    public bool WriteVmafLogs { get; init; }

    /// <summary>What a probe of a file's timeline answers with. Null makes the probe fail.</summary>
    public string? ProbeOutput { get; init; }

    /// <summary>Exits cleanly having written nothing, as a broken encoder does.</summary>
    public bool ProducesNothing { get; init; }

    /// <summary>How long an encode takes, so a test can outlast a lease renewal interval.</summary>
    public TimeSpan EncodeTakes { get; init; }

    public long? SizeBudgetExceededAtBytes { get; init; }
    public OutputSizeBudget? LastSizeBudget { get; private set; }

    public Task<TranscodeResult> RunAsync(
        string ffmpeg, IReadOnlyList<string> arguments,
        IProgress<double>? encodedSeconds, CancellationToken cancellationToken,
        OutputSizeBudget? sizeBudget = null)
    {
        LastSizeBudget = sizeBudget;
        if (sizeBudget is not null && SizeBudgetExceededAtBytes is { } observed)
            return Task.FromResult(new TranscodeResult(-1, "Size saving budget exceeded.", observed));
        Arguments = arguments;
        AllRuns.Add(arguments);
        encodedSeconds?.Report(12.5);

        if (EncodeTakes > TimeSpan.Zero)
        {
            // Cooperative, like the real one: a lease that is genuinely lost stops the encode
            // rather than waiting for it.
            Task.Delay(EncodeTakes, cancellationToken).GetAwaiter().GetResult();
        }

        if (WriteVmafLogs && LogPathIn(arguments) is { } log)
        {
            // Frames as well as the pooled figure, because a real libvmaf log carries both and
            // two different readers depend on it: the server pools the frames, and the alignment
            // probe scores them. A log with only the pooled figure made every candidate offset
            // unscoreable, so the alignment silently found nothing.
            File.WriteAllText(
                log,
                """
                {"frames":[{"metrics":{"vmaf":95.0}},{"metrics":{"vmaf":95.0}}],
                 "pooled_metrics":{"vmaf":{"harmonic_mean":95.0}}}
                """);
            return Task.FromResult(new TranscodeResult(0, ""));
        }

        // Where the command says, not where the test says. A measurement ends in the null muxer
        // and writes nothing, which is how a run with no file to produce is told apart from one
        // that produced nothing it should have.
        var output = writeCandidate ?? OutputPathIn(arguments);
        if (output is not null && exitCode == 0 && !ProducesNothing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "encoded-bytes");
        }
        return Task.FromResult(new TranscodeResult(exitCode, errorTail));
    }

    public Task<ProbeResult> ProbeAsync(
        string ffprobe, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Probes.Add(arguments);
        return Task.FromResult(ProbeOutput is null
            ? new ProbeResult(1, string.Empty)
            : new ProbeResult(0, ProbeOutput));
    }

    /// <summary>The file this command would write, or nothing for a command that writes no file.</summary>
    private static string? OutputPathIn(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var last = arguments[^1];
        // "-f null -" is the null muxer: a measurement, which produces a log and no file.
        return last is "-" or "" ? null : last;
    }

    /// <summary>Reads the log path back out of the filter, the way FFmpeg would.</summary>
    private static string? LogPathIn(IReadOnlyList<string> arguments)
    {
        var filter = arguments.FirstOrDefault(a => a.Contains("libvmaf", StringComparison.Ordinal));
        if (filter is null)
        {
            return null;
        }

        var marker = filter.IndexOf("log_path=", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var value = filter[(marker + "log_path=".Length)..];
        var end = value.IndexOf(":shortest", StringComparison.Ordinal);
        if (end >= 0)
        {
            value = value[..end];
        }

        // Undo the escaping the sidecar applied for FFmpeg's filter parser.
        return value.Replace(@"\\:", ":", StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar);
    }
}
