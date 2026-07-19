namespace Optimisarr.Core.Queue;

/// <summary>
/// The work root for a single media file's output. Namespacing each file's output by its
/// unique media-file id guarantees two sources that share a stem but differ by extension
/// (e.g. <c>photo.bmp</c> and <c>photo.tif</c>, both targeting <c>photo.webp</c>) never
/// resolve to the same work path and clobber each other's verified output before it is moved
/// or replaced. The id segment lives only under the work root; the final move/replace
/// destination is derived from the source's natural name. Pure and unit tested.
/// </summary>
public static class WorkOutputRoot
{
    public static string ForMediaFile(string workRoot, int mediaFileId) =>
        $"{workRoot.TrimEnd('/', '\\')}/{mediaFileId}";

    /// <summary>
    /// The work root for a throwaway preview job's output. Kept under a dedicated <c>preview/</c>
    /// subtree, keyed by job id, so previews never collide with replace-bound outputs and are
    /// trivial to purge (the whole subtree, or one job's folder) without touching real output.
    /// </summary>
    public static string ForPreview(string workRoot, int jobId) =>
        $"{workRoot.TrimEnd('/', '\\')}/preview/{jobId}";

    /// <summary>Disposable calibration output, isolated from previews and replaceable work.</summary>
    public static string ForCalibration(string workRoot, Guid sessionId, int jobId) =>
        $"{workRoot.TrimEnd('/', '\\')}/calibration/{sessionId:N}/{jobId}";
}
