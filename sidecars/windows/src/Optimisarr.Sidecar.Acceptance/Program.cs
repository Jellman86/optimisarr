using System.Text.Json;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

// Uses the shipped worker's prober, protocol, transfers and job runner. Credentials stay in
// memory; the Windows service's pairing and settings are never opened or changed.
if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("Windows acceptance must run on a real Windows host.");
}

static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing {key}");

var ffmpeg = Required("OPTIMISARR_FFMPEG");
var name = Environment.GetEnvironmentVariable("OPTIMISARR_ACCEPTANCE_NAME") ?? "Windows acceptance";
var scratch = Required("OPTIMISARR_ACCEPTANCE_SCRATCH");
long FreeBytes() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(scratch))!).AvailableFreeSpace;
var capabilities = await new CapabilityProber(new ProcessCommandRunner())
    .ProbeAsync(name, ffmpeg, FreeBytes(), 1);
if (args.Contains("--discover"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { videoEncoders = capabilities.VideoEncoders, operatingSystem = "windows" }));
    return;
}

var encoder = Required("OPTIMISARR_ACCEPTANCE_ENCODER");
if (!capabilities.VideoEncoders.Contains(encoder))
{
    throw new InvalidOperationException($"Encoder failed the production capability probe: {encoder}");
}
if (Directory.Exists(scratch))
{
    throw new InvalidOperationException("Acceptance scratch directory must be new.");
}
Directory.CreateDirectory(scratch);
capabilities = capabilities with { VideoEncoders = [encoder] };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var client = new SidecarClient(http);
var server = Required("OPTIMISARR_ACCEPTANCE_SERVER");
var paired = await client.PairAsync(server, Required("OPTIMISARR_ACCEPTANCE_PIN"), capabilities, cancellation.Token);
var pairing = new StoredPairing(server, paired.Credential, paired.WorkerId);
var runner = new JobRunner(client, new JobTransfer(http), new ProcessTranscoder(), ffmpeg, scratch,
    () => null, Console.WriteLine);
while (!cancellation.IsCancellationRequested)
{
    capabilities = capabilities with { FreeScratchBytes = FreeBytes() };
    var heartbeat = await client.HeartbeatAsync(pairing, capabilities, cancellationToken: cancellation.Token);
    if (!heartbeat.Draining && await client.ClaimAsync(pairing, cancellation.Token) is { } assignment)
    {
        var outcome = await runner.RunAsync(pairing, assignment, cancellation.Token);
        Console.WriteLine($"Job {outcome.JobId}: {outcome.Delivered} {outcome.Detail}");
    }
    await Task.Delay(1000, cancellation.Token);
}
