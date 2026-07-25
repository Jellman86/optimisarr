using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Library;

internal readonly record struct ParsedLibrary(
    string Name,
    string Path,
    MediaType MediaType,
    RuleProfile RuleProfile,
    bool Enabled,
    int Priority,
    long? MinFileSizeBytes,
    int? MaxHeight,
    long? ReencodeSameCodecAboveBytes,
    bool SkipEfficientSources,
    string? TargetVideoCodec,
    string? TargetContainer,
    HdrHandling? HdrHandling,
    bool OptimiseDolbyVision,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    string? AudioTargetCodec,
    int? AudioBitrateKbps,
    string? VideoAudioCodec,
    int? VideoAudioBitrateKbps,
    bool DownmixToStereo,
    string? KeepAudioLanguages,
    string? KeepSubtitleLanguages,
    bool ReencodeLossyAudio,
    string? TargetImageFormat,
    int? ImageQuality,
    bool ReencodeLossyImages,
    ImageDownscaleMode ImageDownscaleMode,
    int ImageDownscaleValue,
    bool MoveOnComplete,
    string? TargetFolder,
    bool MoveOverwrite,
    double? MinVmafHarmonicMean,
    double? MinVmafMin,
    bool? VmafQualityGateEnabled,
    double? MinVmafCatastrophicMin,
    bool? ClipVmafEnabled,
    int? VmafFrameSubsample,
    bool AutoEnqueueEnabled,
    TimeOnly AutoEnqueueWindowStart,
    TimeOnly AutoEnqueueWindowEnd,
    bool AutoReplace);

/// <summary>Validates and normalises a library create/update request.</summary>
internal static class LibraryRequestParser
{
    public static bool TryParse(SaveLibraryRequest request, out ParsedLibrary parsed, out string? error)
    {
        parsed = default;

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A library name is required.";
            return false;
        }

        var path = request.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A library path is required.";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = $"Directory does not exist: {path}";
            return false;
        }

        if (!Enum.TryParse<MediaType>(request.MediaType, ignoreCase: true, out var mediaType))
        {
            error = $"Unknown media type: {request.MediaType}. Expected one of {string.Join(", ", Enum.GetNames<MediaType>())}.";
            return false;
        }

        if (!Enum.TryParse<RuleProfile>(request.RuleProfile, ignoreCase: true, out var ruleProfile))
        {
            error = $"Unknown rule profile: {request.RuleProfile}. Expected one of {string.Join(", ", Enum.GetNames<RuleProfile>())}.";
            return false;
        }

        if (ruleProfile == RuleProfile.TrackCleanup && mediaType is MediaType.Music or MediaType.Photo)
        {
            error = "Track cleanup applies only to Film, TV, or mixed libraries that can contain video files.";
            return false;
        }

        HdrHandling? hdrHandling = null;
        if (!string.IsNullOrWhiteSpace(request.HdrHandling))
        {
            if (!Enum.TryParse<HdrHandling>(request.HdrHandling, ignoreCase: true, out var parsedHdr))
            {
                error = $"Unknown HDR handling: {request.HdrHandling}. Expected one of {string.Join(", ", Enum.GetNames<HdrHandling>())}.";
                return false;
            }
            hdrHandling = parsedHdr;
        }

        if (request.MinFileSizeBytes is < 0)
        {
            error = "Minimum file size cannot be negative.";
            return false;
        }

        if (request.MaxHeight is <= 0)
        {
            error = "Maximum resolution must be greater than zero.";
            return false;
        }

        if (request.ReencodeSameCodecAboveBytes is <= 0)
        {
            error = "The same-codec re-encode size threshold must be greater than zero.";
            return false;
        }

        if (request.QualityCrf is < 0 or > 63)
        {
            error = "Quality (CRF) must be between 0 and 63.";
            return false;
        }

        if (!EncoderPresetPolicy.TryNormaliseSelection(request.EncoderPreset, out var encoderPreset))
        {
            error = "Encoder effort must be encoder default, quick, balanced, efficient, or a recognised legacy preset.";
            return false;
        }

        if (request.MinVmafHarmonicMean is < 0 or > 100
            || request.MinVmafMin is < 0 or > 100
            || request.MinVmafCatastrophicMin is < 0 or > 100)
        {
            error = "VMAF overrides must be between 0 and 100.";
            return false;
        }

        if (request.VmafFrameSubsample is < 1 or > QualityScoreCommandBuilder.MaximumFrameSubsample)
        {
            error = $"VMAF frame sampling must be between 1 and {QualityScoreCommandBuilder.MaximumFrameSubsample}.";
            return false;
        }

        if ((request.MinVmafMin is { } fifth && request.MinVmafHarmonicMean is { } harmonic && fifth > harmonic)
            || (request.MinVmafCatastrophicMin is { } catastrophic
                && request.MinVmafMin is { } fifthFloor
                && catastrophic > fifthFloor)
            || (request.MinVmafCatastrophicMin is { } catastrophicFloor
                && request.MinVmafHarmonicMean is { } harmonicFloor
                && catastrophicFloor > harmonicFloor))
        {
            error = "VMAF floors must be ordered: catastrophic frame ≤ fifth percentile ≤ harmonic mean.";
            return false;
        }

        var audioTargetCodec = Trim(request.AudioTargetCodec);
        if (audioTargetCodec is not null && !AudioTarget.IsSupportedTarget(audioTargetCodec))
        {
            error = $"Unknown audio codec: {audioTargetCodec}. Expected one of {string.Join(", ", AudioTarget.SupportedCodecs)}.";
            return false;
        }

        if (request.AudioBitrateKbps is < 32 or > 512)
        {
            error = "Audio bitrate must be between 32 and 512 kbps.";
            return false;
        }

        var videoAudioCodec = Trim(request.VideoAudioCodec);
        if (videoAudioCodec is not null
            && !videoAudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase)
            && !AudioTarget.IsSupportedTarget(videoAudioCodec))
        {
            error = $"Unknown video audio codec: {videoAudioCodec}. Expected copy or one of {string.Join(", ", AudioTarget.SupportedCodecs)}.";
            return false;
        }

        if (request.VideoAudioBitrateKbps is < 32 or > 512)
        {
            error = "Video audio bitrate must be between 32 and 512 kbps.";
            return false;
        }

        if (!TryParseLanguageList(request.KeepAudioLanguages, out var keepAudioLanguages))
        {
            error =
                $"Audio languages must be comma-separated, registered individual ISO 639 codes " +
                $"and at most {TrackLanguages.MaxLanguageListLength} characters (e.g. \"eng, jpn\").";
            return false;
        }

        if (!TryParseLanguageList(request.KeepSubtitleLanguages, out var keepSubtitleLanguages))
        {
            error =
                $"Subtitle languages must be comma-separated, registered individual ISO 639 codes " +
                $"and at most {TrackLanguages.MaxLanguageListLength} characters (e.g. \"eng, jpn\").";
            return false;
        }

        var targetImageFormat = Trim(request.TargetImageFormat);
        if (targetImageFormat is not null && !ImageTarget.IsEncodable(targetImageFormat))
        {
            error = $"Unsupported image format: {targetImageFormat}. Expected one of {string.Join(", ", ImageTarget.EncodableFormats)}.";
            return false;
        }

        if (request.ImageQuality is < 1 or > 100)
        {
            error = "Image quality must be between 1 and 100.";
            return false;
        }

        var downscaleMode = ImageDownscaleMode.None;
        if (!string.IsNullOrWhiteSpace(request.ImageDownscaleMode)
            && !Enum.TryParse(request.ImageDownscaleMode, ignoreCase: true, out downscaleMode))
        {
            error = "Image downscale mode must be one of None, MaxLongEdge, or Percent.";
            return false;
        }

        var downscaleValue = request.ImageDownscaleValue ?? 0;
        if (downscaleMode == ImageDownscaleMode.MaxLongEdge && downscaleValue is < 16 or > 100_000)
        {
            error = "Image max long-edge must be between 16 and 100000 pixels.";
            return false;
        }
        if (downscaleMode == ImageDownscaleMode.Percent && downscaleValue is < 1 or > 99)
        {
            error = "Image downscale percentage must be between 1 and 99.";
            return false;
        }

        if (!TryParseWindowTime(request.AutoEnqueueWindowStart, out var autoStart))
        {
            error = "Auto-enqueue window start must use HH:mm format.";
            return false;
        }

        if (!TryParseWindowTime(request.AutoEnqueueWindowEnd, out var autoEnd))
        {
            error = "Auto-enqueue window end must use HH:mm format.";
            return false;
        }

        var moveOnComplete = request.MoveOnComplete ?? false;
        var targetFolder = Trim(request.TargetFolder);
        if (moveOnComplete)
        {
            if (targetFolder is null)
            {
                error = "A target folder is required when 'move output on complete' is enabled.";
                return false;
            }

            if (!Directory.Exists(targetFolder))
            {
                error = $"Target folder does not exist: {targetFolder}";
                return false;
            }
        }

        parsed = new ParsedLibrary(
            name,
            path,
            mediaType,
            ruleProfile,
            request.Enabled ?? true,
            request.Priority ?? 0,
            request.MinFileSizeBytes,
            request.MaxHeight,
            request.ReencodeSameCodecAboveBytes,
            request.SkipEfficientSources ?? true,
            Trim(request.TargetVideoCodec),
            Trim(request.TargetContainer),
            hdrHandling,
            request.OptimiseDolbyVision ?? false,
            Trim(request.ExcludePaths),
            request.QualityCrf,
            encoderPreset,
            audioTargetCodec is null ? null : audioTargetCodec.ToLowerInvariant(),
            request.AudioBitrateKbps,
            videoAudioCodec is null ? null : videoAudioCodec.ToLowerInvariant(),
            request.VideoAudioBitrateKbps,
            request.DownmixToStereo ?? false,
            keepAudioLanguages,
            keepSubtitleLanguages,
            request.ReencodeLossyAudio ?? false,
            targetImageFormat is null ? null : targetImageFormat.ToLowerInvariant(),
            request.ImageQuality,
            request.ReencodeLossyImages ?? false,
            downscaleMode,
            downscaleMode == ImageDownscaleMode.None ? 0 : downscaleValue,
            moveOnComplete,
            targetFolder,
            request.MoveOverwrite ?? false,
            request.MinVmafHarmonicMean,
            request.MinVmafMin,
            request.VmafQualityGateEnabled,
            request.MinVmafCatastrophicMin,
            request.ClipVmafEnabled,
            request.VmafFrameSubsample,
            request.AutoEnqueueEnabled ?? false,
            autoStart,
            autoEnd,
            request.AutoReplace ?? false);
        error = null;
        return true;
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // Kept languages (audio and subtitle alike) are stored as a canonical comma-separated list
    // of lower-case ISO 639 codes ("eng, jpn"). Blank input means "keep every track" and stores null.
    private static bool TryParseLanguageList(string? value, out string? normalised)
        => TrackLanguages.TryNormaliseLanguageList(value, out normalised);

    // An omitted window time defaults to 00:00; start == end means the window is open
    // all day (resolved by AutoEnqueueScheduleEvaluator), i.e. "auto-enqueue once a day".
    private static bool TryParseWindowTime(string? value, out TimeOnly time)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            time = default;
            return true;
        }

        return TimeOnly.TryParseExact(
            value.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out time);
    }
}
