using System.Text.Json;
using Optimisarr.Core.Diagnostics;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Revalidates stored data at export, including databases written by older builds.</summary>
internal static class DiagnosticDetailSanitizer
{
    private static readonly IReadOnlySet<string> Numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "effectiveVideoQuality", "requestedVideoQuality", "parentAttempt", "outputSizeBytes", "protocolVersion", "maxCandidateBytes", "minCandidateBytes",
        "originalSizeBytes", "newSizeBytes", "sequence", "jobId", "encodedSeconds", "offsetBytes", "transferBytes", "httpStatus"
    };
    private static readonly IReadOnlySet<string> Booleans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "preferSoftwareDecode", "verificationPassed", "crossFilesystem", "detailsTruncated" };
    public static JsonElement? Read(string? json)
    {
        if (json is null || json.Length > 4096) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (Numbers.Contains(p.Name) && p.Value.TryGetDoubleSafe() is { } number && double.IsFinite(number)) result[p.Name] = p.Value.Clone();
                else if (Booleans.Contains(p.Name) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) result[p.Name] = p.Value.GetBoolean();
                else if (p.Value.ValueKind == JsonValueKind.String)
                {
                    var value = p.Value.GetString();
                    string? safe = p.Name switch
                    {
                        "sidecarVersion" => DiagnosticSafeFields.Version(value),
                        "serverVersion" => DiagnosticSafeFields.Version(value),
                        "outcome" => DiagnosticSafeFields.AttemptOutcome(value),
                        "retryReason" => DiagnosticSafeFields.AttemptReason(value),
                        "encoder" or "videoEncoder" => DiagnosticSafeFields.Encoder(value),
                        "decoder" => DiagnosticSafeFields.Decoder(value),
                        "operatingSystem" => DiagnosticSafeFields.OperatingSystem(value),
                        "architecture" => value is "arm64" or "x64" or "x86" ? value : null,
                        "identitySource" => value is "AttemptTimeSnapshot" or "EventTimeSnapshot" ? value : null,
                        "verificationLocation" => DiagnosticSafeFields.VerificationLocation(value),
                        "reasonCode" => DiagnosticSafeFields.EventReason(value),
                        "stage" => DiagnosticTelemetry.Stage(value),
                        "state" => value is "Held" or "Released" or "Completed" or "Expired" ? value : null,
                        "endReason" => value is "Reclaimed" or "Operator" or "Worker" or "Expired" ? value : null,
                        "sourceSha256" or "verifiedSourceSha256" or "candidateSha256" or "originalSha256" or "outputSha256" or "reportSha256" or "workSha256" or "contractSha256" or "qualityContractSha256" or "evidenceSha256" or "commandSha256" or "ffmpegSha256" or "ffprobeSha256" or "measurementFfmpegSha256" => DiagnosticTelemetry.Sha256(value),
                        "attemptId" => value is { Length: <= 32 } && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9]+:[0-9]+\z") ? value : null,
                        "leaseId" => Guid.TryParse(value, out var id) ? id.ToString() : null,
                        "occurredAt" => DateTimeOffset.TryParse(value, out var date) ? date.ToString("O") : null,
                        _ => null
                    };
                    if (safe is not null) result[p.Name] = safe;
                }
                else if (p.Name == "process" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    var summary = p.Value.Deserialize<DiagnosticProcessSummary>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (summary is not null) result[p.Name] = DiagnosticProcessSummary.Sanitize(summary);
                }
                else if (p.Name == "report" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    var snapshot = p.Value.Deserialize<DiagnosticVerificationSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (snapshot is not null) result[p.Name] = DiagnosticVerificationSnapshot.Sanitize(snapshot);
                }
                else if (p.Name == "scheduling" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    var snapshot = p.Value.Deserialize<DiagnosticSchedulingSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
                    if (snapshot is not null && Enum.IsDefined(snapshot.Reason)) result[p.Name] = snapshot;
                }
                else if (p.Name == "evidence" && p.Value.ValueKind == JsonValueKind.Object)
                    result[p.Name] = p.Value.Deserialize<DiagnosticExecutionEvidence>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                else if (p.Name == "policy" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    var fields = new HashSet<string> { "durationTolerancePercent", "requireAudioRetained", "requireSubtitlesRetained", "requireSizeReduction", "minimumSizeSavingPercent", "maximumSizeSavingPercent", "vmafQualityGateEnabled", "minVmafHarmonicMean", "minVmafMin", "minVmafCatastrophicMin", "vmafFrameSubsample", "audioQualityReportingEnabled", "audioQualityGateEnabled", "maximumAudioQualityDistance", "soundtrackQualityReportingEnabled", "soundtrackQualityGateEnabled", "maximumSoundtrackQualityDistance", "autoReplace", "imageQualityGateEnabled", "minimumImageSsim", "imageMetadataGateEnabled" };
                    result[p.Name] = p.Value.EnumerateObject().Where(v => fields.Contains(v.Name) &&
                        (v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False || v.Value.TryGetDoubleSafe() is { } n && double.IsFinite(n)))
                        .GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.Last().Value.Clone());
                }
                else if (p.Name == "encoders" && p.Value.ValueKind == JsonValueKind.Array)
                    result[p.Name] = p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => DiagnosticSafeFields.Encoder(v.GetString())).Where(v => v is not null).Take(20).ToArray();
            }
            return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        }
        catch (JsonException) { return null; }
    }
    private static double? TryGetDoubleSafe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}
