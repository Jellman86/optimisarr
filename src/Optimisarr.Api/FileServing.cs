using Microsoft.AspNetCore.StaticFiles;
using Optimisarr.Core.Library;

namespace Optimisarr.Api;

internal static class FileServing
{
    public static IResult ServeLibraryFile(string path, string? libraryRoot)
    {
        var stream = OpenLibraryRead(path, libraryRoot);
        if (stream is null) return Results.NotFound();
        var contentType = new FileExtensionContentTypeProvider().TryGetContentType(path, out var resolved)
            ? resolved : "application/octet-stream";
        return Results.File(stream, contentType, enableRangeProcessing: true);
    }

    public static FileStream? OpenLibraryRead(string path, string? libraryRoot)
    {
        if (!LibraryPathSafety.IsSafeFile(path, libraryRoot)) return null;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, useAsync: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (!LibraryPathSafety.IsSafeFile(path, libraryRoot))
        {
            stream.Dispose();
            return null;
        }
        return stream;
    }

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
