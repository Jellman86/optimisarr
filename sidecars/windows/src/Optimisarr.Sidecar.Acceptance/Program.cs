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
var client = new SidecarClient(http, new DiagnosticJournal(Path.Combine(scratch, "diagnostics"), ffmpeg, Environment.GetEnvironmentVariable("OPTIMISARR_FFPROBE")));
var server = Required("OPTIMISARR_ACCEPTANCE_SERVER");
var runner = new JobRunner(client, new JobTransfer(http, client.Diagnostics), new ProcessTranscoder(), ffmpeg, scratch,
    () => null, Console.WriteLine);
// Use the service's recovery, heartbeat and claim lifecycle, including server restarts.
var session = new SidecarSession(client, new InMemoryCredentialStore(),
    _ => Task.FromResult(capabilities), () => null, Task.Delay,
    status => Console.WriteLine($"Session: {status.State} {status.Detail}"),
    (pairing, assignment, token) => runner.RunAsync(pairing, assignment, token), FreeBytes);
await session.PairAsync(server, Required("OPTIMISARR_ACCEPTANCE_PIN"), cancellation.Token);
await session.RunAsync(cancellation.Token);
