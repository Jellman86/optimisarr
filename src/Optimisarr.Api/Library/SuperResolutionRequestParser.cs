using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Api.Library;

/// <summary>
/// Validates the super-resolution engine section of a settings request. Unsupported
/// combinations — a preset/scale/denoise triple the shipped model set does not contain, an
/// unknown device, an out-of-range value — are rejected with a specific error code, never
/// silently substituted: an engine run must be exactly what the operator configured.
/// </summary>
internal static class SuperResolutionRequestParser
{
    public static bool TryParse(
        SuperResolutionSettingsDto request,
        out SuperResolutionOptions options,
        out SettingsRequestError? error)
    {
        options = request.ToOptions();
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            error = new SettingsRequestError(problems[0].Code, problems[0].Message);
            return false;
        }

        error = null;
        return true;
    }
}
