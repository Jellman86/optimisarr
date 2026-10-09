namespace Optimisarr.Core.Diagnostics;

public sealed record DiagnosticCaptureOptions(
    bool PersistAcrossRestart = false,
    int RetentionDays = 7,
    int FailureRetentionDays = 30,
    long MaximumBytes = 4 * 1024 * 1024);

/// <summary>Only typed, bounded telemetry crosses the diagnostic boundary. No free text or paths.</summary>
public sealed record SidecarDiagnosticEvent(
    long Sequence, DateTimeOffset OccurredAt, Guid LeaseId, int JobId, string ReasonCode,
    string? Stage = null, double? EncodedSeconds = null, long? OffsetBytes = null,
    int? HttpStatus = null, string? FfmpegSha256 = null, string? FfprobeSha256 = null, string? MeasurementFfmpegSha256 = null);
public sealed record SidecarDiagnosticBatch(int SchemaVersion, Guid SessionId, Guid InstanceId,
    IReadOnlyList<SidecarDiagnosticEvent> Events, bool Final = false, long DroppedEvents = 0, bool RecoveryIncomplete = false);
public sealed record SidecarDiagnosticConsent(Guid SessionId, DateTimeOffset ServerTimeUtc,
    DateTimeOffset? ExpiresAt, int? ScopedJobId, bool Recording = true);

public static class DiagnosticTelemetry
{
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "Worker.AssignmentReceived", "Worker.StageChanged", "Worker.LeaseRenewed", "Worker.LeaseReleased",
        "Worker.VerificationAcknowledged", "Worker.TransferOffset", "Worker.TransferAcknowledged",
        "Worker.RequestFailed", "Worker.ToolsIdentified"
    };
    public static string? Stage(string? value) => value is "FetchingSource" or "Encoding" or "Measuring" or "Delivering" ? value : null;
    public static string? Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    public static SidecarDiagnosticEvent Sanitize(SidecarDiagnosticEvent value) => value with
    {
        Stage = Stage(value.Stage),
        EncodedSeconds = value.EncodedSeconds is >= 0 and <= 1_000_000 && double.IsFinite(value.EncodedSeconds.Value) ? value.EncodedSeconds : null,
        OffsetBytes = value.OffsetBytes is >= 0 ? value.OffsetBytes : null,
        HttpStatus = value.HttpStatus is >= 100 and <= 599 ? value.HttpStatus : null,
        FfmpegSha256 = Sha256(value.FfmpegSha256), FfprobeSha256 = Sha256(value.FfprobeSha256), MeasurementFfmpegSha256 = Sha256(value.MeasurementFfmpegSha256)
    };
}
