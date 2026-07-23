namespace Optimisarr.Core.Queue;

/// <summary>
/// The class of a job failure, derived from its recorded error message. Stable buckets so the
/// diagnostics API and UI can group "why jobs fail" without re-parsing free text everywhere.
/// </summary>
public enum FailureCategory
{
    SizeSaving,
    Verification,
    ContainerIncompatibility,
    BitmapSubtitles,
    ReplacementCollision,
    SourceMissing,
    InvalidConfiguration,
    Other
}

/// <summary>
/// Maps a job's free-text error message onto a <see cref="FailureCategory"/>. Pure string matching,
/// ordered most-specific first, so it is unit tested and shared between the API aggregation and the
/// per-job display.
/// </summary>
public static class FailureClassifier
{
    public static FailureCategory Classify(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return FailureCategory.Other;
        }

        bool Has(string fragment) => errorMessage.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        // Checked before the generic "Verification failed" below, because the size-saving message is
        // itself a verification message ("Verification failed: Size saving").
        if (Has("Size saving"))
        {
            return FailureCategory.SizeSaving;
        }

        if (Has("text to text or bitmap to bitmap") || Has("image-based subtitles"))
        {
            return FailureCategory.BitmapSubtitles;
        }

        if (Has("codec none") || Has("incorrect codec parameters") || Has("not currently supported in container"))
        {
            return FailureCategory.ContainerIncompatibility;
        }

        if (Has("collide with an existing file") || Has("Another optimised file"))
        {
            return FailureCategory.ReplacementCollision;
        }

        if (Has("no longer exists") || Has("missing from the work directory") || Has("No such file"))
        {
            return FailureCategory.SourceMissing;
        }

        if (Has("Invalid encoder effort")
            || Has("Encoder effort") && Has("cannot be resolved")
            || Has("Error setting option preset"))
        {
            return FailureCategory.InvalidConfiguration;
        }

        if (Has("Verification failed"))
        {
            return FailureCategory.Verification;
        }

        return FailureCategory.Other;
    }

    /// <summary>A short, operator-facing explanation of a category, for the diagnostics view.</summary>
    public static string Describe(FailureCategory category) => category switch
    {
        FailureCategory.SizeSaving => "Output was not smaller than the source (size-saving gate)",
        FailureCategory.Verification => "A verification gate rejected the output",
        FailureCategory.ContainerIncompatibility => "A stream the target container can't hold",
        FailureCategory.BitmapSubtitles => "Image-based subtitles the MP4 container can't store",
        FailureCategory.ReplacementCollision => "Destination already occupied by another optimised file",
        FailureCategory.SourceMissing => "Source or verified output no longer on disk",
        FailureCategory.InvalidConfiguration => "A saved option is not valid for the selected encoder",
        _ => "Other / unclassified"
    };
}
