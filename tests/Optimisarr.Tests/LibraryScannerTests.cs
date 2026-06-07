using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-scan-").FullName;
    private readonly LibraryScanner _scanner = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Scan_returns_settled_media_files()
    {
        var movie = WriteFile("Movies/Example (2020)/Example.mkv", modifiedMinutesAgo: 30);

        var result = _scanner.Scan(_root, new LibraryScanOptions(), _now);

        var file = Assert.Single(result.Files);
        Assert.Equal(movie, file.AbsolutePath);
        Assert.Equal(Path.Combine("Movies", "Example (2020)", "Example.mkv"), file.RelativePath);
        Assert.True(file.SizeBytes > 0);
    }

    [Fact]
    public void Scan_ignores_non_media_files()
    {
        WriteFile("Movies/Example.mkv", modifiedMinutesAgo: 30);
        WriteFile("Movies/Example.nfo", modifiedMinutesAgo: 30);
        WriteFile("Movies/poster.jpg", modifiedMinutesAgo: 30);

        var result = _scanner.Scan(_root, new LibraryScanOptions(), _now);

        Assert.Single(result.Files);
        Assert.Equal(2, result.SkippedNonMedia);
    }

    [Fact]
    public void Scan_skips_files_modified_within_the_settling_period()
    {
        WriteFile("Movies/StillCopying.mkv", modifiedMinutesAgo: 0);

        var options = new LibraryScanOptions { SettlingPeriod = TimeSpan.FromMinutes(2) };
        var result = _scanner.Scan(_root, options, _now);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedUnsettled);
    }

    [Fact]
    public void Scan_throws_when_root_is_missing()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        Assert.Throws<DirectoryNotFoundException>(() => _scanner.Scan(missing, new LibraryScanOptions(), _now));
    }

    private string WriteFile(string relativePath, int modifiedMinutesAgo)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "test-content");
        File.SetLastWriteTimeUtc(fullPath, _now.UtcDateTime.AddMinutes(-modifiedMinutesAgo));
        return fullPath;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
