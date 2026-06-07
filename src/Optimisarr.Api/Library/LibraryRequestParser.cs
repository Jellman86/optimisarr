using Optimisarr.Core.Domain;

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
    string? TargetVideoCodec,
    string? TargetContainer,
    HdrHandling? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    bool MoveOnComplete,
    string? TargetFolder);

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

        if (request.QualityCrf is < 0 or > 63)
        {
            error = "Quality (CRF) must be between 0 and 63.";
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
            Trim(request.TargetVideoCodec),
            Trim(request.TargetContainer),
            hdrHandling,
            Trim(request.ExcludePaths),
            request.QualityCrf,
            Trim(request.EncoderPreset),
            moveOnComplete,
            targetFolder);
        error = null;
        return true;
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
