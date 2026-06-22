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
    public static bool CanMoveAtomically(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var source = Path.Combine(sourceDirectory, $".optimisarr-move-probe-{Guid.NewGuid():N}.tmp");
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));

        try
        {
            File.WriteAllBytes(source, Array.Empty<byte>());
            File.Move(source, destination);
            return true;
        }
        // A non-writable directory throws UnauthorizedAccessException (Linux: "Permission
        // denied"), which is not an IOException — catch it too so a permissions problem is
        // reported as "can't move" rather than crashing the request.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(source);
            TryDelete(destination);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover empty probe file is harmless.
        }
    }

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
