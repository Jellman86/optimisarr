using System.Runtime.InteropServices;

namespace Optimisarr.Sidecar.Native;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Credentials and assignment data belong exclusively to the inherited private pipe.
        if (args.Length != 0) return 2;
        using var stopped = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; NativeHostProtocol.CancelOwnedWork(stopped); };
        using var term = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; NativeHostProtocol.CancelOwnedWork(stopped); }) : null;
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        return await NativeHostProgram.RunAsync(input, output, stopped.Token);
    }
}
