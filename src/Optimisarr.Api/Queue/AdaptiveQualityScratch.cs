namespace Optimisarr.Api.Queue;

/// <summary>Owns cleanup of one adaptive-quality sample without removing its shared workspace.</summary>
internal static class AdaptiveQualityScratch
{
    public static void DeleteSample(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The enclosing adaptive search removes the complete scratch directory in its finally
            // block. A transient sample-cleanup failure must not invalidate otherwise valid VMAF
            // evidence or remove the workspace needed by the remaining windows.
        }
    }
}
