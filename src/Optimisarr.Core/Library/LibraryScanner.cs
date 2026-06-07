namespace Optimisarr.Core.Library;

/// <summary>A media file discovered on disk during a library scan.</summary>
public sealed record ScannedFile(
    string AbsolutePath,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record LibraryScanOptions
{
    /// <summary>
    /// A file must not have been modified within this window to be considered
    /// settled. This avoids picking up files that are still being written.
    /// </summary>
    public TimeSpan SettlingPeriod { get; init; } = TimeSpan.FromMinutes(2);

    public IReadOnlySet<string> Extensions { get; init; } = LibraryScanner.DefaultMediaExtensions;
}

public sealed record LibraryScanResult(
    IReadOnlyList<ScannedFile> Files,
    int SkippedUnsettled,
    int SkippedNonMedia);

/// <summary>
/// Walks a library root and returns settled media files. Pure filesystem logic
/// with no persistence so it can be unit tested without a database.
/// </summary>
public sealed class LibraryScanner
{
    public static readonly IReadOnlySet<string> DefaultMediaExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv",
            ".ts", ".m2ts", ".mts", ".flv", ".webm", ".mpg", ".mpeg"
        };

    public LibraryScanResult Scan(string root, LibraryScanOptions options, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Library root does not exist: {root}");
        }

        var files = new List<ScannedFile>();
        var skippedUnsettled = 0;
        var skippedNonMedia = 0;

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", enumerationOptions))
        {
            var extension = System.IO.Path.GetExtension(path);
            if (!options.Extensions.Contains(extension))
            {
                skippedNonMedia++;
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (IOException)
            {
                continue;
            }

            var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (nowUtc - modifiedAt < options.SettlingPeriod)
            {
                skippedUnsettled++;
                continue;
            }

            files.Add(new ScannedFile(
                path,
                System.IO.Path.GetRelativePath(root, path),
                info.Length,
                modifiedAt));
        }

        return new LibraryScanResult(files, skippedUnsettled, skippedNonMedia);
    }
}
