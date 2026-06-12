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
    /// <summary>Video container extensions — the default scan set (Film/TV libraries).</summary>
    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv",
            ".ts", ".m2ts", ".mts", ".flv", ".webm", ".mpg", ".mpeg"
        };

    /// <summary>Audio-only file extensions (Music libraries).</summary>
    public static readonly IReadOnlySet<string> AudioExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".mp3", ".m4a", ".m4b", ".aac", ".opus", ".ogg", ".oga",
            ".wav", ".wma", ".aiff", ".aif", ".ape", ".wv", ".mka", ".dsf", ".dff"
        };

    /// <summary>Still-image extensions (Photo libraries) — the same set the kind classifier recognises.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = Domain.MediaKindClassifier.ImageExtensions;

    /// <summary>The default scan set is video, preserving long-standing behaviour for callers that
    /// do not specify a media type (and for Film/TV libraries).</summary>
    public static readonly IReadOnlySet<string> DefaultMediaExtensions = VideoExtensions;

    /// <summary>
    /// The file extensions a scan should discover for a library of the given media type, so a
    /// Music library finds audio, a Photo library finds images, a Film/TV library finds video,
    /// and a mixed "Other" library finds all three. Keyed off the library's type rather than
    /// scanning everything everywhere, so a Film library never hoovers up stray poster images.
    /// </summary>
    public static IReadOnlySet<string> ExtensionsFor(Domain.MediaType mediaType) => mediaType switch
    {
        Domain.MediaType.Music => AudioExtensions,
        Domain.MediaType.Photo => ImageExtensions,
        Domain.MediaType.Other => new HashSet<string>(
            VideoExtensions.Concat(AudioExtensions).Concat(ImageExtensions), StringComparer.OrdinalIgnoreCase),
        _ => VideoExtensions
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
