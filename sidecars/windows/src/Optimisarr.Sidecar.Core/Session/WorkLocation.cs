namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Where this machine's job source, candidate and scratch files live, and how a non-default
/// location is chosen and kept.
/// </summary>
/// <para>
/// The default sits on the system drive, which is rarely where anyone wants hundreds of gigabytes
/// of transcode scratch. An operator can move it in two mutually consistent ways, both honoured
/// here so an update or a service-tool switch cannot silently throw it back to C::
/// <list type="number">
///   <item>a durable override file (written by the tray or an operator, survives reinstalls);</item>
///   <item>the <c>OPTIMISARR_SIDECAR_WORK</c> environment variable (the same knob the Linux worker
///   uses).</item>
/// </list>
/// A "memory" work location is just a folder on a volume backed by RAM (a RAM drive the operator
/// mounts — e.g. ImDisk on Windows, or a tmpfs mount on Linux). It needs no special code here
/// beyond sanity-checking the path, because the volume's size bounds it: the server only offers a
/// job when this machine reports enough free scratch for it, so a bounded RAM volume naturally
/// keeps every job that runs on it inside the budget — the same bounded behaviour Linux gets from
/// a <c>size=</c> tmpfs mount, with jobs that will not fit simply never being offered here.
/// </para>
public static class WorkLocation
{
    /// <summary>The override file the tray (or an operator) writes under ProgramData alongside the
    /// pairing and diagnostics. A plain single line; empty or missing means "unspecified".</summary>
    public static string DefaultOverrideFileName => "work-location";

    public const string DefaultPath = @"C:\OptimisarrWork";

    /// <summary>
    /// Pick the first usable location from, in order: the override file, the environment variable,
    /// then the default. Anything that is empty, whitespace, or a bare volume root (which cannot be
    /// a scratch directory) is skipped rather than trusted.
    /// </summary>
    public static string Resolve(string? fromOverrideFile, string? fromEnvironment)
    {
        return UsablePath(fromOverrideFile, out var fromFile)
            ? fromFile
            : UsablePath(fromEnvironment, out var fromEnv)
                ? fromEnv
                : DefaultPath;
    }

    private static bool UsablePath(string? raw, out string path)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            path = string.Empty;
            return false;
        }

        var root = Path.GetPathRoot(trimmed);
        // A bare drive root ("C:\", "D:", a UNC share root) or a drive-relative name cannot hold
        // per-job scratch folders; the work dir has to be a real rooted subdirectory.
        if (root is null || !Path.IsPathRooted(trimmed)
            || trimmed.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            return false;
        }

        path = trimmed.TrimEnd('\\', '/');
        return true;
    }
}
