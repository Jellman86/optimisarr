using Optimisarr.Core.Domain;
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
    public void A_music_library_discovers_audio_and_ignores_video_and_images()
    {
        WriteFile("Album/Track.flac", modifiedMinutesAgo: 30);
        WriteFile("Album/cover.jpg", modifiedMinutesAgo: 30);
        WriteFile("Album/bonus.mkv", modifiedMinutesAgo: 30);

        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(MediaType.Music) };
        var result = _scanner.Scan(_root, options, _now);

        var file = Assert.Single(result.Files);
        Assert.EndsWith("Track.flac", file.RelativePath);
    }

    [Fact]
    public void A_photo_library_discovers_images_and_ignores_video_and_audio()
    {
        WriteFile("Photos/IMG_1.png", modifiedMinutesAgo: 30);
        WriteFile("Photos/clip.mp4", modifiedMinutesAgo: 30);
        WriteFile("Photos/song.mp3", modifiedMinutesAgo: 30);

        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(MediaType.Photo) };
        var result = _scanner.Scan(_root, options, _now);

        var file = Assert.Single(result.Files);
        Assert.EndsWith("IMG_1.png", file.RelativePath);
    }

    [Fact]
    public void An_other_library_discovers_video_audio_and_images()
    {
        WriteFile("Mixed/movie.mkv", modifiedMinutesAgo: 30);
        WriteFile("Mixed/track.flac", modifiedMinutesAgo: 30);
        WriteFile("Mixed/photo.jpg", modifiedMinutesAgo: 30);
        WriteFile("Mixed/notes.nfo", modifiedMinutesAgo: 30);

        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(MediaType.Other) };
        var result = _scanner.Scan(_root, options, _now);

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(1, result.SkippedNonMedia);
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

    [PosixFact]
    public void Scan_skips_leaf_and_directory_links_even_when_targets_are_settled()
    {
        var original = WriteFile("real/movie.mkv", modifiedMinutesAgo: 30);
        File.CreateSymbolicLink(Path.Combine(_root, "leaf.mkv"), original);
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.GetDirectoryName(original)!);
        using var outside = new TemporaryDirectory();
        var outsideFile = Path.Combine(outside.Path, "outside.mkv");
        File.WriteAllText(outsideFile, "outside");
        File.SetLastWriteTimeUtc(outsideFile, _now.UtcDateTime.AddHours(-1));
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside.Path);

        var result = _scanner.Scan(_root, new LibraryScanOptions(), _now);

        Assert.Equal(original, Assert.Single(result.Files).AbsolutePath);
    }

    [PosixFact]
    public void Scan_does_not_descend_into_a_directory_cycle()
    {
        var original = WriteFile("movie.mkv", modifiedMinutesAgo: 30);
        Directory.CreateSymbolicLink(Path.Combine(_root, "cycle"), _root);
        Assert.Equal(original, Assert.Single(_scanner.Scan(_root, new LibraryScanOptions(), _now).Files).AbsolutePath);
    }

    [PosixFact]
    public void Scan_skips_file_links_even_without_a_settling_period()
    {
        var original = WriteFile("movie.mkv", modifiedMinutesAgo: 30);
        File.CreateSymbolicLink(Path.Combine(_root, "link.mkv"), original);
        var options = new LibraryScanOptions { SettlingPeriod = TimeSpan.Zero };
        Assert.Equal(original, Assert.Single(_scanner.Scan(_root, options, _now).Files).AbsolutePath);
    }

    [PosixFact]
    public void Scan_refuses_a_linked_library_root()
    {
        WriteFile("movie.mkv", modifiedMinutesAgo: 30);
        var alias = _root + "-alias";
        Directory.CreateSymbolicLink(alias, _root);
        try
        {
            Assert.Throws<IOException>(() => _scanner.Scan(alias, new LibraryScanOptions(), _now));
            Assert.Throws<IOException>(() => _scanner.Scan(alias + Path.DirectorySeparatorChar, new LibraryScanOptions(), _now));
        }
        finally { Directory.Delete(alias); }
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("optimisarr-scan-outside-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
