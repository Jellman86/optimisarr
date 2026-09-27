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
var dashboard = new WorkerDashboard(options.Name, options.Server ?? new FileCredentialStore(options.Config).Load()?.ServerAddress, options.Scratch, options.Concurrency);
SidecarCapabilities? proved = null;
async Task<SidecarCapabilities> Probe(CancellationToken ct)
{
    proved ??= await prober.ProbeAsync(options.Name, options.Ffmpeg, FreeBytes(), options.Concurrency, ct);
    if (options.Encoder is { Length: > 0 } encoder)
    {
        if (!proved.VideoEncoders.Contains(encoder)) throw new InvalidOperationException("Configured encoder failed its real capability probe.");
        proved = proved with { VideoEncoders = [encoder] };
    }
    dashboard.Capabilities(proved);
    return proved with { FreeScratchBytes = FreeBytes() };
}
if (args.Contains("--discover"))
{
    Console.WriteLine(JsonSerializer.Serialize(await Probe(token), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}
await using var web = Environment.GetEnvironmentVariable("OPTIMISARR_WEB_ENABLED") == "true"
    ? DashboardHost.Create(dashboard) : null;
if (web is not null) await web.StartAsync(token);
using var control = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var bulk = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var client = new SidecarClient(control);
var store = new FileCredentialStore(options.Config);
var runner = new JobRunner(client, new JobTransfer(bulk), new ProcessTranscoder(), options.Ffmpeg,
    options.Scratch, () => null, Console.WriteLine, measurementFfmpegPath: options.MeasurementFfmpeg,
    allowLinuxDevices: true, observe: dashboard.Observe);
var healthGate = new object();
SidecarSession? session = null;
session = new SidecarSession(client, store, Probe, () => null, Task.Delay,
    report: status =>
    {
        Console.WriteLine($"{status.State}: {status.Detail}");
        dashboard.Status(status, session?.ServerDraining == true || session?.IsPaused == true);
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
    var pairing = store.Load();
    if (pairing is null)
    {
        var codeFile = Environment.GetEnvironmentVariable("OPTIMISARR_PAIRING_CODE_FILE");
        var pin = codeFile is null ? Environment.GetEnvironmentVariable("OPTIMISARR_PAIRING_CODE")
            : (await File.ReadAllTextAsync(codeFile, token)).Trim();
        if (string.IsNullOrWhiteSpace(pin) || options.Server is null)
            throw new InvalidOperationException("First start requires OPTIMISARR_SERVER and a pairing code or pairing-code file.");
        await session.PairAsync(options.Server, pin, token);
        Environment.SetEnvironmentVariable("OPTIMISARR_PAIRING_CODE", null);
    }
    else if (options.Server is not null && !string.Equals(options.Server.TrimEnd('/'), pairing.ServerAddress.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The configured server differs from the saved pairing. Use a separate config volume to pair another server.");
    await session.RunAsync(token);
    return token.IsCancellationRequested ? 0 : 1;
}
catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
finally
{
    File.Delete(health);
    if (web is not null) await web.StopAsync(CancellationToken.None);
}
