using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

/// <summary>
/// The sidecar's entry point.
///
///     Optimisarr.Sidecar --pair https://optimisarr.example.com    (pairing code on stdin)
///     Optimisarr.Sidecar                                          (check in until stopped)
///
/// <para><b>Pairing reads the code from standard input, not from an argument.</b> A command line is
/// visible to every other account on the machine through the process list, and lands in shell
/// history besides. The code is short-lived and single-use, but it is still the one secret that
/// buys a credential.</para>
///
/// <para><b>Headless pairing is not a convenience here, it is the only option.</b> This runs as a
/// Windows service under an account nobody logs into; there is no window to type a code into and no
/// session to show one in. A sidecar that could only be paired by hand could not be installed
/// remotely, scripted onto several machines, or recovered after a credential was revoked.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (Has(args, "--install"))
        {
            return ServiceControl.Install();
        }

        if (Has(args, "--uninstall"))
        {
            return ServiceControl.Uninstall();
        }

        var pairIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--pair", StringComparison.OrdinalIgnoreCase));
        if (pairIndex >= 0)
        {
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopping.Cancel();
            };
            return await PairAsync(args, pairIndex, stopping.Token);
        }

        return await RunHostAsync(args);
    }

    public static async Task PairForTrayAsync(string address, string pin, CancellationToken token)
    {
        var session = Build(out _);
        await session.PairAsync(address, pin, token);
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the check-in loop under the generic host, as a Windows service when Windows started it
    /// and as an ordinary console process otherwise.
    ///
    /// <para>The same executable does both deliberately. Being able to run it in a terminal and
    /// watch it is how anyone will diagnose a machine that is misbehaving, and a service that can
    /// only be observed through the Event Log is one nobody can debug.</para>
    /// </summary>
    private static async Task<int> RunHostAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options => options.ServiceName = ServiceControl.ServiceName);

        // Where a headless machine gets to explain itself. Console logging is useless under a
        // service with no console, and the Event Log is the one place an operator will think to
        // look on a Windows box.
        builder.Logging.AddEventLog(settings => settings.SourceName = ServiceControl.ServiceName);
        // The Event Log provider keeps Warning and above by default, so without this the sidecar
        // would say everything it does into a sink that drops all of it — which is how the first
        // attempt at giving this service a voice appeared to work and wrote nothing at all.
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(
            null, LogLevel.Information);

        builder.Services.AddSingleton<WorkerMonitor>();
        builder.Services.AddSingleton<HostShutdown>();
        builder.Services.AddHostedService(services => services.GetRequiredService<HostShutdown>());
        builder.Services.AddHostedService<MonitorServer>();
        builder.Services.AddSingleton(services =>
        {
            // Wired here rather than in Build: pairing runs before there is a host, and a console
            // is the right place for its output. Everything else runs headless and has to leave a
            // record somewhere, or a machine that hands every job straight back looks identical to
            // one that is simply never offered any.
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Optimisarr.Sidecar");
            var session = Build(
                out Func<CancellationToken, Task<SidecarCapabilities>> _,
                report: status => Log(logger, status),
                reportJob: line => logger.LogInformation("{Line}", line),
                monitor: services.GetRequiredService<WorkerMonitor>());
            return session;
        });
        builder.Services.AddHostedService<SidecarWorker>();

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    private static async Task<int> PairAsync(string[] args, int pairIndex, CancellationToken cancellationToken)
    {
        if (pairIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: Optimisarr.Sidecar --pair <server address>   (pairing code on stdin)");
            return 2;
        }

        var serverAddress = args[pairIndex + 1];
        var pin = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            Console.Error.WriteLine("No pairing code on stdin.");
            return 2;
        }

        var session = Build(out var probe);
        try
        {
            var pairing = await session.PairAsync(serverAddress, pin, cancellationToken);
            var capabilities = await probe(cancellationToken);
            Console.WriteLine($"Paired with {serverAddress} as worker {pairing.WorkerId}.");
            // Said plainly at pairing rather than left to be discovered as silence: the server's
            // capability matching is fail-closed, so a machine that proved no encoders is never
            // offered anything and would otherwise look simply ignored.
            if (capabilities.VideoEncoders.Count == 0)
            {
                Console.WriteLine(
                    "This machine proved no video encoders, so the server will not offer it work "
                    + "until FFmpeg is available to it.");
            }
            return 0;
        }
        catch (SidecarException exception)
        {
            Console.Error.WriteLine($"Pairing failed: {exception.Message}");
            if (!serverAddress.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // A bare host is reached over http, which is right on a home network and wrong
                // behind a TLS proxy — where it fails with nothing to suggest the scheme.
                Console.Error.WriteLine(
                    $"Tried http://{serverAddress} — give the address as https://{serverAddress} "
                    + "if the server is behind TLS.");
            }
            return 1;
        }
    }

    /// <summary>
    /// Where a job's source and candidate would live. Overridable because the default sits on the
    /// system drive, which is rarely where anyone wants hundreds of gigabytes of scratch video.
    /// </summary>
    internal static string ScratchDirectory() =>
        Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_WORK")
        ?? @"C:\OptimisarrWork";

    /// <summary>
    /// Free space where the work would actually happen, not on the system drive. The server uses
    /// this to decide whether to offer a job at all, so reporting the wrong volume would have it
    /// hand over work this machine has nowhere to put.
    /// </summary>
    internal static long FreeScratchBytes(string scratch)
    {
        try
        {
            Directory.CreateDirectory(scratch);
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(scratch))!).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing readable means claim nothing: zero free space keeps the server from offering
            // work rather than sending it somewhere unwritable.
            return 0;
        }
    }

    /// <summary>
    /// The FFmpeg this sidecar will use, in the order that gives the most predictable answer.
    ///
    /// The bundled copy comes first. It is pinned, hash-checked and proved by
    /// <c>scripts/fetch-ffmpeg.ps1</c>, so it is the only one whose capabilities are known; whatever
    /// happens to be on PATH may lack NVENC, or libvmaf, or both, and the operator would never be
    /// told — the worker would simply never be offered the work it was installed for.
    ///
    /// An explicit override still wins over everything, for the machine that has a better build
    /// than the one shipped.
    /// </summary>
    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        // Beside the executable once installed; up in vendor/ when run from a build tree.
        var here = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(here, "ffmpeg.exe"),
                     Path.Combine(here, "vendor", "ffmpeg.exe"),
                     Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "vendor", "ffmpeg.exe")),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing a probe over.
            }
        }

        return null;
    }

    /// <summary>
    /// How a status reaches the Event Log. A fault or a stop is an error, an unreachable server is
    /// a warning because it usually comes back on its own, and everything else is ordinary
    /// progress — so a machine can be triaged by severity rather than by reading every line.
    /// </summary>
    /// <summary>The last status written, so an unchanged one is not written again.</summary>
    private static SessionStatus? _lastLogged;

    private static void Log(ILogger logger, SessionStatus status)
    {
        // Only when it changes. The loop reports a status every check-in, which is every thirty
        // seconds for ever — nearly three thousand identical "connected" entries a day, in the one
        // place an operator goes to find out what went wrong. A repeated status carries no
        // information; a changed one carries all of it.
        if (_lastLogged == status)
        {
            return;
        }

        _lastLogged = status;

        switch (status.State)
        {
            case SidecarState.Faulted or SidecarState.Stopped:
                logger.LogError("{State}: {Detail}", status.State, status.Detail);
                break;
            case SidecarState.Unreachable:
                logger.LogWarning("{State}: {Detail}", status.State, status.Detail);
                break;
            default:
                logger.LogInformation("{State}: {Detail}", status.State, status.Detail);
                break;
        }
    }

    private static SidecarSession Build(
        out Func<CancellationToken, Task<SidecarCapabilities>> probe,
        Action<SessionStatus>? report = null,
        Action<string>? reportJob = null,
        WorkerMonitor? monitor = null)
    {
        var prober = new CapabilityProber(new ProcessCommandRunner());
        var scratch = ScratchDirectory();

        probe = token => prober.ProbeAsync(
            Environment.MachineName,
            // Whatever FFmpeg this machine has on its PATH, until the installer bundles one. Null
            // when there is none, which the prober reports as no capabilities at all rather than
            // guessing — and a worker that proved nothing is never offered work.
            FindFfmpeg(),
            FreeScratchBytes(scratch),
            // One job at a time until there is a job runner to run a second with.
            maxConcurrency: 1,
            token);
        var capture = probe;

        // One client for control traffic, another for transfers. A whole-video upload must not sit
        // behind the same 30-second timeout as a check-in, and a check-in must not queue behind a
        // 40 GB download on a connection-limited handler.
        var control = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bulk = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new SidecarClient(control);
        var loadSampler = new MachineLoadSampler();

        var runner = new JobRunner(
            client,
            new JobTransfer(bulk),
            new ProcessTranscoder(),
            FindFfmpeg() ?? "ffmpeg.exe",
            scratch,
            loadSampler.Sample,
            reportJob,
            observe: monitor is null ? null : monitor.Observe,
            wantsPreview: monitor is null ? null : () => monitor.WantsPreviews,
            publishPreview: monitor is null ? null : (jobId, jpeg) => monitor.PublishPreview(jobId, jpeg));

        return new SidecarSession(
            client,
            new DpapiCredentialStore(),
            capture,
            load: loadSampler.Sample,
            delay: Task.Delay,
            report: report,
            runJob: async (pairing, assignment, token) =>
            {
                try
                {
                    var result = await runner.RunAsync(pairing, assignment, token);
                    monitor?.Complete(assignment.JobId, result.Detail);
                    return result;
                }
                finally { monitor?.Remove(assignment.JobId); }
            });
    }
}
