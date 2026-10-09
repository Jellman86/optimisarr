using System.Runtime.InteropServices;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Linux;

var options = WorkerOptions.Read(Environment.GetEnvironmentVariable);
var health = Path.Combine(options.Config, "health");
if (args.Contains("--healthcheck"))
    return File.Exists(health) && DateTime.UtcNow - File.GetLastWriteTimeUtc(health) < TimeSpan.FromSeconds(100) ? 0 : 1;
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run this host on Linux.");
// A second process using this pairing could claim duplicate capacity and delete another job's scratch.
using var instance = new WorkerDirectoryLock(options.Config);
using var scratchInstance = new WorkerDirectoryLock(options.Scratch);
File.Delete(health);
using var cancellation = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
var prober = new CapabilityProber(new ProcessCommandRunner(), platform: "linux",
    measurementFfmpeg: options.MeasurementFfmpeg, linuxDevices: true);
long FreeBytes() => new DriveInfo(Path.GetFullPath(options.Scratch)).AvailableFreeSpace;
var metrics = new LinuxWorkerMetrics();
var dashboard = new WorkerDashboard(options.Name, options.Server ?? new FileCredentialStore(options.Config).Load()?.ServerAddress, options.Scratch, options.Concurrency, () => metrics.Current);
// One probe shared by pairing, the session and the eager start below, so an early pairing from
// the page never runs a second set of real encodes beside the first. A failed probe is retried.
var probeGate = new object();
Task<SidecarCapabilities>? probing = null;
async Task<SidecarCapabilities> Prove()
{
    var proved = await prober.ProbeAsync(options.Name, options.Ffmpeg, FreeBytes(), options.Concurrency, token);
    if (options.Encoder is { Length: > 0 } encoder)
    {
        if (!proved.VideoEncoders.Contains(encoder)) throw new InvalidOperationException("Configured encoder failed its real capability probe.");
        proved = proved with { VideoEncoders = [encoder] };
    }
    dashboard.Capabilities(proved);
    return proved;
}
async Task<SidecarCapabilities> Probe(CancellationToken ct)
{
    Task<SidecarCapabilities> task;
    lock (probeGate)
        task = probing = probing is null || probing.IsFaulted || probing.IsCanceled ? Prove() : probing;
    return (await task.WaitAsync(ct)) with { FreeScratchBytes = FreeBytes() };
}
if (args.Contains("--discover"))
{
    Console.WriteLine(JsonSerializer.Serialize(await Probe(token), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}
await using var web = Environment.GetEnvironmentVariable("OPTIMISARR_WEB_ENABLED") == "true"
    ? DashboardHost.Create(dashboard) : null;
if (web is not null)
{
    await web.StartAsync(token);
    // The page shows what this machine proved while it waits to be paired, and the first pairing
    // then does not wait for the probe.
    _ = Probe(token).ContinueWith(probe => _ = probe.Exception, TaskScheduler.Default);
}
var metricsTask = metrics.RunAsync(token);
using var control = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var bulk = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var client = new SidecarClient(control, new DiagnosticJournal(options.Config, options.Ffmpeg, Path.Combine(Path.GetDirectoryName(options.Ffmpeg) ?? "", "ffprobe"), options.MeasurementFfmpeg));
dashboard.Diagnostics = client.Diagnostics;
var store = new FileCredentialStore(options.Config);
var runner = new JobRunner(client, new JobTransfer(bulk, client.Diagnostics), new ProcessTranscoder(), options.Ffmpeg,
    options.Scratch, metrics.Load, Console.WriteLine, measurementFfmpegPath: options.MeasurementFfmpeg,
    allowLinuxDevices: true, observe: dashboard.Observe,
    wantsPreview: () => dashboard.WantsPreview, publishPreview: dashboard.Preview);
var healthGate = new object();
SidecarSession? session = null;
session = new SidecarSession(client, store, Probe, metrics.Load, Task.Delay,
    report: status =>
    {
        Console.WriteLine($"{status.State}: {status.Detail}");
        dashboard.Status(status, session?.ServerDraining == true || session?.IsPaused == true);
        if (session is not null) dashboard.Server(session.BrandStyle, session.AvailableUpdate);
        lock (healthGate)
        {
            if (status.State is SidecarState.Connected or SidecarState.Working)
                File.WriteAllText(health, DateTimeOffset.UtcNow.ToString("O"));
            else File.Delete(health);
        }
    },
    runJob: async (pairing, assignment, ct) =>
    {
        if (assignment.SourceBytes <= 0 || assignment.SourceBytes > long.MaxValue / 2
            || FreeBytes() < assignment.SourceBytes * 2)
        {
            await client.ReleaseAsync(pairing, assignment.LeaseId, ct);
            return new JobOutcome(assignment.JobId, false, "Insufficient scratch capacity or invalid source size.");
        }
        dashboard.Start(assignment);
        _ = ShowArtworkAsync(pairing, assignment, ct);
        var succeeded = false;
        try
        {
            var outcome = await runner.RunAsync(pairing, assignment, ct);
            succeeded = outcome.Delivered;
            return outcome;
        }
        finally { dashboard.Finish(assignment.JobId, succeeded); }
    }, availableScratchBytes: FreeBytes);
try
{
    // A code from the environment is one-time, so it is tried once per process at most.
    var codeTried = false;
    string? problem = null;
    while (true)
    {
        var pairing = store.Load();
        if (pairing is null)
        {
            string? pin = null;
            if (!codeTried)
            {
                codeTried = true;
                var codeFile = Environment.GetEnvironmentVariable("OPTIMISARR_PAIRING_CODE_FILE");
                pin = codeFile is null ? Environment.GetEnvironmentVariable("OPTIMISARR_PAIRING_CODE")
                    : (await File.ReadAllTextAsync(codeFile, token)).Trim();
                Environment.SetEnvironmentVariable("OPTIMISARR_PAIRING_CODE", null);
            }
            if (!string.IsNullOrWhiteSpace(pin) && options.Server is not null)
            {
                try { pairing = await session.PairAsync(options.Server, pin, token); }
                catch (Exception error) when (web is not null && error is SidecarException or HttpRequestException)
                {
                    problem = error is SidecarException ? error.Message : "Could not reach the configured server to redeem the pairing code.";
                }
            }
            if (pairing is null)
            {
                if (web is null)
                    throw new InvalidOperationException(problem ?? "First start requires OPTIMISARR_SERVER and a pairing code or pairing-code file, or OPTIMISARR_WEB_ENABLED=true to pair from the dashboard.");
                Console.WriteLine("Unpaired: waiting for a pairing code on the dashboard.");
                pairing = await dashboard.Pairing.WaitAsync(options.Server, problem, session.PairAsync, token);
            }
            dashboard.Paired(pairing.ServerAddress);
            problem = null;
        }
        else if (options.Server is not null && !string.Equals(options.Server.TrimEnd('/'), pairing.ServerAddress.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured server differs from the saved pairing. Use a separate config volume to pair another server.");
        await session.RunAsync(token);
        if (token.IsCancellationRequested) return 0;
        // The session returns early only when the server refused the credential, which it has
        // already discarded. With a dashboard the worker waits to be paired again instead of
        // restarting into the same refusal.
        if (web is null || store.Load() is not null) return 1;
        problem = session.Status.Detail;
    }
}
catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
finally
{
    cancellation.Cancel();
    await metricsTask;
    File.Delete(health);
    if (web is not null) await web.StopAsync(CancellationToken.None);
}

// A recognition aid only: any failure leaves the plain placeholder and costs the job nothing.
async Task ShowArtworkAsync(StoredPairing pairing, Assignment assignment, CancellationToken ct)
{
    try
    {
        if (await client.ArtworkAsync(pairing, assignment.LeaseId, ct) is { } artwork)
            dashboard.Artwork(assignment.JobId, artwork);
    }
    catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or SidecarException) { }
}
