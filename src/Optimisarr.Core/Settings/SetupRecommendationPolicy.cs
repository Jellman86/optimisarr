using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Settings;

public enum SetupVmafTier
{
    Off,
    Balanced
}

public sealed record SetupRecommendation(
    EncoderMode EncoderMode,
    bool HardwareDecode,
    SetupVmafTier VmafTier,
    TimeOnly ScheduleStart,
    TimeOnly ScheduleEnd,
    string EncoderReason,
    string VmafReason);

/// <summary>
/// Turns proved runtime capabilities into conservative, visible first-run recommendations.
/// The caller must still ask the operator before applying any recommendation.
/// </summary>
public static class SetupRecommendationPolicy
{
    public static SetupRecommendation Recommend(
        IReadOnlyList<EncoderCapability> encoders,
        bool vmafAvailable,
        bool cudaVmafAvailable)
    {
        var encoder = RecommendEncoder(encoders);
        var hardwareDecode = encoder is not EncoderMode.Cpu;
        var vmafTier = vmafAvailable && cudaVmafAvailable && encoder == EncoderMode.NvidiaNvenc
            ? SetupVmafTier.Balanced
            : SetupVmafTier.Off;

        return new SetupRecommendation(
            encoder,
            hardwareDecode,
            vmafTier,
            new TimeOnly(1, 0),
            new TimeOnly(6, 0),
            encoder switch
            {
                EncoderMode.NvidiaNvenc => "nvidia",
                EncoderMode.IntelQsv => "intel",
                EncoderMode.Vaapi => "vaapi",
                _ => "cpu"
            },
            vmafTier == SetupVmafTier.Balanced
                ? "cuda-balanced"
                : vmafAvailable ? "cpu-cost" : "unavailable");
    }

    private static EncoderMode RecommendEncoder(IReadOnlyList<EncoderCapability> encoders)
    {
        if (Available(encoders, "NVIDIA NVENC", "hevc")) return EncoderMode.NvidiaNvenc;
        if (Available(encoders, "Intel QSV", "hevc")) return EncoderMode.IntelQsv;
        if (Available(encoders, "VAAPI", "hevc")) return EncoderMode.Vaapi;
        return EncoderMode.Cpu;
    }

    private static bool Available(IReadOnlyList<EncoderCapability> encoders, string mode, string codec) =>
        encoders.Any(encoder => encoder.Available && encoder.Mode == mode && encoder.Codec == codec);
}
