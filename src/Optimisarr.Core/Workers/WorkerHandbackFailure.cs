namespace Optimisarr.Core.Workers;

/// <summary>
/// What a worker reports when it hands a job back, such as the end of FFmpeg's error output.
/// It is the worker's account, so it is bounded and stored as text, never acted on except to be
/// shown and matched against known failures.
/// </summary>
public static class WorkerHandbackFailure
{
    /// <summary>The longest report kept. FFmpeg's own explanation is at the end, so the end is kept.</summary>
    public const int MaxLength = 8000;

    /// <summary>The report as stored, or null when the worker said nothing useful.</summary>
    public static string? Normalise(string? failure)
    {
        var text = failure?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxLength ? text : text[^MaxLength..];
    }
}
