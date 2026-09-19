using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

internal static class MonitorClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<MonitorSnapshot> RequestAsync(byte command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await Gate.WaitAsync(timeout.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", MonitorProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new[] { command }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var header = new byte[4];
            await pipe.ReadExactlyAsync(header, timeout.Token);
            var length = BitConverter.ToInt32(header);
            if (length <= 0 || length > MonitorProtocol.MaximumResponseBytes) throw new IOException("Worker response length is invalid.");
            var response = new byte[length];
            await pipe.ReadExactlyAsync(response, timeout.Token);
            await pipe.WriteAsync(new byte[] { 0 }, timeout.Token);
            return JsonSerializer.Deserialize<MonitorSnapshot>(response) ?? throw new IOException("Worker response was empty.");
        }
        finally { Gate.Release(); }
    }
}
