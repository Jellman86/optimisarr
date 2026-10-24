using System.Text.Json;
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
        Split(worker.AudioEncoders),
        Split(worker.HardwareDecoders),
        worker.Vmaf,
        worker.FreeScratchBytes,
        worker.MaxConcurrency,
        worker.SidecarVersion,
        worker.ProtocolVersion,
        ReadPerceptual(worker.PerceptualCapabilitiesJson));

    internal static readonly JsonSerializerOptions PerceptualJson = new(JsonSerializerDefaults.Web);

    internal static string? WritePerceptual(WorkerPerceptualCapabilities? proof) =>
        proof is null ? null : JsonSerializer.Serialize(proof, PerceptualJson);

    private static WorkerPerceptualCapabilities? ReadPerceptual(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return null;
        try
        {
            var proof = JsonSerializer.Deserialize<WorkerPerceptualCapabilities>(json, PerceptualJson);
            return proof?.Valid() == true ? proof : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
