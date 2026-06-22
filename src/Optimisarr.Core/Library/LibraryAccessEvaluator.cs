namespace Optimisarr.Core.Library;

/// <summary>
/// Turns the raw filesystem-access facts about a library path (does it exist, can we read it,
/// can we write it) into a pass/fail verdict and a plain-language explanation. Pure so it is
/// unit tested; the API layer does the actual probing and passes the booleans in. Writability
/// matters because the safe-replacement step must move the original out and the optimised file
/// in — a read-only mount lets scanning work but makes every replacement fail.
/// </summary>
public static class LibraryAccessEvaluator
{
    /// <summary>The library is fully usable only when it exists and is both readable and writable.</summary>
    public static bool IsOk(bool exists, bool readable, bool writable) => exists && readable && writable;

    /// <summary>
    /// A short message describing the most important problem (or success), worst-first so the
    /// user sees the blocking issue rather than a less severe one.
    /// </summary>
    public static string Describe(bool exists, bool readable, bool writable)
    {
        if (!exists)
        {
            return "The library path does not exist in the container. Check the volume mount and the path.";
        }

        if (!readable)
        {
            return "The path exists but Optimisarr can't read it. Give the container's user (PUID/PGID) read access.";
        }

        if (!writable)
        {
            return "Readable but not writable: scanning and probing work, but replacing originals will fail. "
                + "Give the container's user (PUID/PGID) write access to this path.";
        }

        return "Optimisarr can read and write this library.";
    }
}
