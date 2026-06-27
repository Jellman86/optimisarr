using Microsoft.AspNetCore.StaticFiles;

namespace Optimisarr.Api;

internal static class FileServing
{
    /// <summary>
    /// Serves a media file from an absolute, database-sourced path with range support so the browser
    /// can seek video/audio. Returns 404 when the path is missing or the file no longer exists.
    /// </summary>
    public static IResult ServeFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Results.NotFound();
        }

        var contentType = new FileExtensionContentTypeProvider().TryGetContentType(path, out var resolved)
            ? resolved
            : "application/octet-stream";
        return Results.File(path, contentType, enableRangeProcessing: true);
    }
}
