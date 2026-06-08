namespace Optimisarr.Api.Replacement;

public sealed record FileMoveResult(bool CrossFilesystem);

/// <summary>
/// Moves a file safely between locations that may be on different mounts. An atomic
/// rename is tried first; when the destination is on another filesystem the rename
/// fails, so we fall back to copy-plus-verify-plus-delete: the source is only
/// removed once the copy exists and its length matches. The destination is never
/// overwritten — callers guarantee a free destination, so an existing file means
/// something is wrong and we refuse rather than clobber data.
/// </summary>
public static class FileMover
{
    public static FileMoveResult Move(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source file does not exist.", source);
        }

        if (File.Exists(destination))
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            File.Move(source, destination);
            return new FileMoveResult(CrossFilesystem: false);
        }
        catch (IOException)
        {
            // Most likely a cross-device link error: copy, verify size, then delete.
            return CopyVerifyDelete(source, destination);
        }
    }

    private static FileMoveResult CopyVerifyDelete(string source, string destination)
    {
        var expectedLength = new FileInfo(source).Length;
        File.Copy(source, destination, overwrite: false);

        if (new FileInfo(destination).Length != expectedLength)
        {
            File.Delete(destination);
            throw new IOException(
                $"Copy verification failed: {destination} size does not match the source.");
        }

        File.Delete(source);
        return new FileMoveResult(CrossFilesystem: true);
    }
}
