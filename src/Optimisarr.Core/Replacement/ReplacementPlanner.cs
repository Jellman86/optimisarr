using System.Globalization;

namespace Optimisarr.Core.Replacement;

/// <summary>
/// The three paths a replacement moves between: where the original lives, where it
/// will be quarantined, and where the verified output will land.
/// </summary>
public sealed record ReplacementPlan(string OriginalPath, string FinalPath, string QuarantinePath);

/// <summary>
/// Pure path arithmetic for a safe replacement — no I/O, so it is fully unit
/// tested. The verified output takes the original's directory and base name but
/// the output's extension (a transcode may change container, e.g. .avi to .mkv).
/// The original is quarantined under a timestamped, replacement-keyed folder so concurrent
/// replacements of same-named files never collide and rollback always has a unique source.
/// </summary>
public static class ReplacementPlanner
{
    public static ReplacementPlan Plan(
        string originalPath,
        string workOutputPath,
        string trashRoot,
        DateTimeOffset nowUtc,
        string replacementKey)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(originalPath);
        var outputExtension = Path.GetExtension(workOutputPath);
        var finalPath = Path.Combine(directory, baseName + outputExtension);

        var stamp = nowUtc.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        var quarantinePath = Path.Combine(
            trashRoot,
            $"{stamp}-{replacementKey}",
            Path.GetFileName(originalPath));

        return new ReplacementPlan(originalPath, finalPath, quarantinePath);
    }
}
