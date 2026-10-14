using System.Text.Json;
using System.Threading.Channels;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Native;

internal sealed record NativeHostEvent(string Type, int? JobId = null, string? Stage = null, double? EncodedSeconds = null,
    string? Detail = null, WorkerPerceptualCapabilities? Capabilities = null, JobOutcome? Outcome = null, int Version = 1,
    long? NativeMemoryBytes = null, long? RequiredScratchBytes = null);

/// <summary>Keep latest progress bounded while preserving the final result and parent liveness.</summary>
internal sealed class NativeHostEvents
{
    private readonly object _gate = new();
    private readonly Channel<NativeHostEvent> _events = Channel.CreateBounded<NativeHostEvent>(new BoundedChannelOptions(32)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private readonly string? _credential;
    private bool _finished;

    public NativeHostEvents(Stream output, Action broken, string? credential, CancellationToken token)
    {
        _credential = credential;
        _writer = WriteAsync(output, broken, token);
    }

    public void Report(NativeHostEvent value)
    {
        lock (_gate)
            if (!_finished) _events.Writer.TryWrite(Safe(value));
    }

    public Task FinishAsync(NativeHostEvent value)
    {
        lock (_gate)
        {
            if (!_finished)
            {
                _events.Writer.TryWrite(Safe(value));
                _finished = true;
                _events.Writer.TryComplete();
            }
        }
        return _writer;
    }

    private NativeHostEvent Safe(NativeHostEvent value) => value with
    {
        Detail = SafeText(value.Detail),
        EncodedSeconds = value.EncodedSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? seconds : null,
        Outcome = value.Outcome is { } outcome ? outcome with { Detail = SafeText(outcome.Detail)! } : null
    };
    private string? SafeText(string? value)
    {
        if (value is null) return null;
        if (!string.IsNullOrEmpty(_credential)) value = value.Replace(_credential, "[redacted]", StringComparison.Ordinal);
        return value.Length > 512 ? value[..512] : value;
    }

    private async Task WriteAsync(Stream output, Action broken, CancellationToken token)
    {
        try
        {
            await foreach (var value in _events.Reader.ReadAllAsync(token))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, NativeHostProtocol.Json);
                if (bytes.Length > 16 * 1024) throw new IOException();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await output.WriteAsync(bytes, deadline.Token).AsTask().WaitAsync(deadline.Token);
                await output.WriteAsync(new byte[] { 10 }, deadline.Token).AsTask().WaitAsync(deadline.Token);
                await output.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            broken();
            throw new IOException("The native host output channel closed or timed out.");
        }
    }
}
