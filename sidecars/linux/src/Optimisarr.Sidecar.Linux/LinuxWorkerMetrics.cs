using Optimisarr.Api.Metrics;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Linux;

public sealed record WorkerMetrics(double? CpuPercent, double? GpuPercent, string? GpuEngine,
    DateTimeOffset SampledAt);

public sealed class LinuxWorkerMetrics
{
    private readonly LinuxSystemMetrics _reader = new();
    private WorkerMetrics _current = new(null, null, null, DateTimeOffset.UtcNow);
    public WorkerMetrics Current => Volatile.Read(ref _current);
    public MachineLoad Load() => new(Current.CpuPercent / 100, Current.GpuPercent / 100);

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    var cpu = _reader.ReadCpu();
                    var gpu = await _reader.ReadGpuAsync(Children(Environment.ProcessId), token);
                    Volatile.Write(ref _current, new(cpu, gpu?.Percent, gpu?.Engine, DateTimeOffset.UtcNow));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Volatile.Write(ref _current, new(null, null, null, DateTimeOffset.UtcNow));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Threads can each own children. Walk only this worker's descendants, including VMAF and
    // previews, so native installs do not attribute somebody else's FFmpeg activity to this worker.
    private static IReadOnlyCollection<int> Children(int parent)
    {
        var found = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(parent);
        while (pending.TryDequeue(out var pid) && found.Count < 256)
        {
            try
            {
                foreach (var thread in Directory.EnumerateDirectories($"/proc/{pid}/task"))
                {
                    try
                    {
                        foreach (var part in File.ReadAllText(Path.Combine(thread, "children")).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(part, out var child) && found.Add(child)) pending.Enqueue(child);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
