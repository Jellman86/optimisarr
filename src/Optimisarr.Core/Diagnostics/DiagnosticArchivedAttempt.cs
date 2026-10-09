using System.Text.Json;

namespace Optimisarr.Core.Diagnostics;

/// <summary>Reads only the retained fields needed to freeze a superseded candidate.</summary>
public sealed record DiagnosticArchivedAttempt(int Number, string? VideoEncoder, string? HardwareDecoder,
    DateTimeOffset EndedAt, bool? VerificationPassed, string? VerificationReportJson, string? Outcome,
    string? Reason, string? FfmpegArguments, string? ProcessLog = null)
{
    public static IReadOnlyList<DiagnosticArchivedAttempt> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<DiagnosticArchivedAttempt>>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [])
                .Where(a => a is not null && a.Number > 0).TakeLast(8).ToArray();
        }
        catch (JsonException) { return []; }
    }
}
