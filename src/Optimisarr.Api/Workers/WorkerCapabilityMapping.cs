using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Rebuilds a worker's proved capabilities from its persisted row. One definition, because the
/// claim that chose an encoder for a worker and the verification that later re-derives the same
/// contract for its delivered candidate must read the row the same way.
/// </summary>
internal static class WorkerCapabilityMapping
{
    public static WorkerCapabilities ToCapabilities(this Worker worker) => new(
        worker.OperatingSystem,
        worker.Architecture,
        Split(worker.VideoEncoders),
        Split(worker.HardwareDecoders),
        worker.Vmaf,
        worker.FreeScratchBytes,
        worker.MaxConcurrency);

    private static IReadOnlyList<string> Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
