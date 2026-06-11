namespace Optimisarr.Core;

/// <summary>
/// The fingerprint Optimisarr writes into every output file's container metadata so a
/// file can prove it was already optimised — independently of any database. The mark
/// travels <em>with the file</em>: move it to another machine, reinstall, or clear the
/// queue history, and the file is still recognised and skipped rather than transcoded a
/// second time. Read back from <c>format.tags</c> by the probe and honoured by the
/// candidate evaluator.
/// </summary>
public static class OptimisationMarker
{
    /// <summary>
    /// The container metadata key, written via ffmpeg <c>-metadata &lt;key&gt;=…</c> and
    /// read back from ffprobe's <c>format.tags</c>. ffprobe may report tag keys in any
    /// case, so lookups are case-insensitive.
    /// </summary>
    public const string MetadataKey = "optimisarr";
}
