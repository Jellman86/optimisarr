using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Diagnostics;

public sealed record DiagnosticGateSnapshot(string Name, string Outcome);
public sealed record DiagnosticVerificationSnapshot(bool Passed, IReadOnlyList<DiagnosticGateSnapshot> Checks,
    double? VmafMean, double? VmafHarmonicMean, double? VmafMinimum, double? VmafFifthPercentile,
    int? FrameCount, string Location, int OmittedCheckCount = 0,
    double? MinimumVmafHarmonicMean = null, double? MinimumVmafFifthPercentile = null, double? MinimumVmafCatastrophicMin = null,
    double? AudioWorstDistance = null, double? MaximumAudioDistance = null,
    double? SoundtrackWorstDistance = null, double? MaximumSoundtrackDistance = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static DiagnosticVerificationSnapshot? Read(string? text)
    {
        if (text is null || text.Length > 1_000_000) return null;
        try
        {
            var report = JsonSerializer.Deserialize<VerificationReport>(text, Json);
            if (report is null || !report.HasValidStructure()) return null;
            var scores = report.Vmaf?.Scores;
            return new(report.Passed, report.Checks.OrderByDescending(c => c.Outcome == CheckOutcome.Failed).Take(32).Select(c => new DiagnosticGateSnapshot(SafeName(c.Name), c.Outcome.ToString())).ToArray(),
                Finite(scores?.VmafMean), Finite(scores?.VmafHarmonicMean), Finite(scores?.VmafMin), Finite(scores?.VmafFifthPercentile), scores?.FrameCount,
                report.Context?.VerificationLocation is "Worker" ? "Worker" : "Server", Math.Max(0, report.Checks.Count - 32),
                Finite(report.Context?.MinimumVmafHarmonicMean), Finite(report.Context?.MinimumVmafFifthPercentile), Finite(report.Context?.MinimumVmafCatastrophicMin),
                Finite(report.AudioQuality?.Evidence?.Assessment?.WorstChannelDistance), Finite(report.AudioQuality?.MaximumDistance),
                Finite(report.SoundtrackQuality?.Tracks?.Where(t => t?.Report?.Evidence?.Assessment is not null)
                    .Select(t => t.Report.Evidence!.Assessment.WorstChannelDistance).Max()), Finite(report.SoundtrackQuality?.MaximumDistance));
        }
        catch (JsonException) { return null; }
    }
    public static DiagnosticVerificationSnapshot Sanitize(DiagnosticVerificationSnapshot value) => value with
    {
        Checks = (value.Checks ?? []).Where(c => c is not null).Take(32).Select(c => new DiagnosticGateSnapshot(SafeName(c.Name), c.Outcome is "Passed" or "Failed" ? c.Outcome : "Unknown")).ToArray(),
        VmafMean = Finite(value.VmafMean), VmafHarmonicMean = Finite(value.VmafHarmonicMean), VmafMinimum = Finite(value.VmafMinimum), VmafFifthPercentile = Finite(value.VmafFifthPercentile),
        Location = value.Location is "Worker" or "Server" ? value.Location : "Unknown",
        OmittedCheckCount = Math.Max(value.OmittedCheckCount, Math.Max(0, (value.Checks?.Count ?? 0) - 32)),
        MinimumVmafHarmonicMean = Finite(value.MinimumVmafHarmonicMean), MinimumVmafFifthPercentile = Finite(value.MinimumVmafFifthPercentile), MinimumVmafCatastrophicMin = Finite(value.MinimumVmafCatastrophicMin),
        AudioWorstDistance = Finite(value.AudioWorstDistance), MaximumAudioDistance = Finite(value.MaximumAudioDistance),
        SoundtrackWorstDistance = Finite(value.SoundtrackWorstDistance), MaximumSoundtrackDistance = Finite(value.MaximumSoundtrackDistance)
    };
    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
    private static string SafeName(string? name) => name is "A/V sync" or "Audio clipping (true peak)" or "Audio codecs unchanged" or "Audio fidelity"
        or "Audio languages" or "Audio loudness (EBU R128)" or "Audio metadata and artwork" or "Audio tracks" or "Colour metadata" or "Container unchanged"
        or "Decode health" or "Dimensions" or "Duration" or "File identity" or "HDR signal" or "Image metadata (EXIF/ICC)" or "Image quality (SSIM)"
        or "Output readable" or "Perceptual quality (VMAF)" or "Picture" or "Size saving" or "Compression ceiling" or "Source video timeline"
        or "Subtitle languages" or "Subtitle tracks" or "Tail integrity" or "Timestamp integrity" or "Video stream" or "Video structure" ? name : "Other verification check";
}
