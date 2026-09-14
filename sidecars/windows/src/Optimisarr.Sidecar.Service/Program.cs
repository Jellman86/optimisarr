using System.Runtime.Versioning;
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
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Stop the check-in loop and let it unwind, rather than being killed mid-request.
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        var pairIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--pair", StringComparison.OrdinalIgnoreCase));

        return pairIndex >= 0
            ? await PairAsync(args, pairIndex, stopping.Token)
            : await RunAsync(stopping.Token);
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

    private static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var session = Build(out _);
        await session.RunAsync(cancellationToken);
        return session.Status.State switch
        {
            SidecarState.Unpaired => 2,
            SidecarState.Stopped => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Where a job's source and candidate would live. Overridable because the default sits on the
    /// system drive, which is rarely where anyone wants hundreds of gigabytes of scratch video.
    /// </summary>
    private static string ScratchDirectory() =>
        Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_WORK")
        ?? @"C:\OptimisarrWork";

    /// <summary>
    /// Free space where the work would actually happen, not on the system drive. The server uses
    /// this to decide whether to offer a job at all, so reporting the wrong volume would have it
    /// hand over work this machine has nowhere to put.
    /// </summary>
    private static long FreeScratchBytes(string scratch)
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
    /// FFmpeg on PATH, or none. Found by asking the system rather than guessing an install path, so
    /// a winget, Chocolatey or hand-unzipped copy all work the same way.
    /// </summary>
    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_SIDECAR_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
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

    private static SidecarSession Build(
        out Func<CancellationToken, Task<SidecarCapabilities>> probe)
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

        return new SidecarSession(
            new SidecarClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }),
            new DpapiCredentialStore(),
            capture,
            // Load reporting arrives with the job runner; nothing is claimed until it can be
            // measured, because an invented figure is worse than an absent one.
            load: () => null,
            delay: Task.Delay,
            report: status => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  {status.Detail}"));
    }
}
