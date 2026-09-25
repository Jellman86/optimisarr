namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Names the file an assignment's command writes.
///
/// <para>The output placeholder is a <em>prefix</em>: the server sends
/// <c>{{output}}.mkv</c> in the command and <c>mkv</c> — no leading dot — as the assignment's
/// extension, because the extension is part of the encode contract and the worker must not guess
/// it. Rebuilding the name by concatenating the two produced <c>candidatemkv</c>, so FFmpeg wrote
/// one file and this machine looked for another. It exited cleanly and the sidecar reported that
/// it had produced nothing.</para>
/// </summary>
public static class CandidatePath
{
    public static string For(string prefixWithoutExtension, string extension)
    {
        var trimmed = extension.TrimStart('.');
        return trimmed.Length == 0 ? prefixWithoutExtension : $"{prefixWithoutExtension}.{trimmed}";
    }
}
