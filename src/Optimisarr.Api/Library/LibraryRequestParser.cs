using Optimisarr.Data;

namespace Optimisarr.Api.Library;

internal readonly record struct ParsedLibrary(
    string Name,
    string Path,
    MediaType MediaType,
    RuleProfile RuleProfile,
    bool Enabled);

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

        parsed = new ParsedLibrary(name, path, mediaType, ruleProfile, request.Enabled ?? true);
        error = null;
        return true;
    }
}
