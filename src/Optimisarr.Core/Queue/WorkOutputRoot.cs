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
}
