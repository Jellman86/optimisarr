namespace Optimisarr.Api.Library;

/// <summary>
/// Probes a directory for the access Optimisarr needs: that it exists, can be read (scanning),
/// and can be written (safe replacement). Used by the per-library access check and as the
/// pre-flight guard before a replacement writes into a media folder. All probes swallow the
/// expected filesystem errors and report a boolean rather than throwing.
/// </summary>
public static class PathAccessProbe
{
    public static (bool Exists, bool Readable, bool Writable) Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return (false, false, false);
        }

        return (true, CanRead(path), CanWrite(path));
    }

    /// <summary>True when a temporary file can be created (and removed) in the directory.</summary>
    public static bool CanWrite(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        var probe = Path.Combine(directory, $".optimisarr-access-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover empty probe file is harmless.
            }
        }
    }

    private static bool CanRead(string directory)
    {
        try
        {
            // Touching the first entry forces the directory listing, which is what fails on a
            // path we lack read/execute permission for.
            using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            enumerator.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
