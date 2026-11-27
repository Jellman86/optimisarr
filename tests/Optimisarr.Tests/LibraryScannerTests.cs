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
    public void Scan_skips_copies_waiting_in_exact_duplicate_quarantine()
    {
        var quarantined = Path.Combine(_root, Optimisarr.Core.Deduplication.DedupeQuarantinePaths.FolderName, "action", "Copy.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantined)!);
        File.WriteAllText(quarantined, "copy");
        File.SetLastWriteTimeUtc(quarantined, _now.UtcDateTime.AddDays(-30));

        var result = _scanner.Scan(_root, new LibraryScanOptions(), _now);

        Assert.Empty(result.Files);
    }

    [Fact]
    public void Scan_skips_a_child_librarys_quarantine_when_scanning_its_parent()
    {
        var quarantined = Path.Combine(_root, "child", Optimisarr.Core.Deduplication.DedupeQuarantinePaths.FolderName, "action", "Copy.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantined)!);
        File.WriteAllText(quarantined, "copy");
        File.SetLastWriteTimeUtc(quarantined, _now.UtcDateTime.AddDays(-30));

        Assert.Empty(_scanner.Scan(_root, new LibraryScanOptions(), _now).Files);
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

    [PosixFact]
    public void ScanFile_returns_one_settled_file_exactly_as_a_library_scan_would()
    {
        var episode = WriteFile("Show/Season 01/S01E01.mkv", modifiedMinutesAgo: 30);

        var scanned = Assert.IsType<SingleFileScan.Settled>(_scanner.ScanFile(_root, episode, new LibraryScanOptions(), _now));

        Assert.Equal(Assert.Single(_scanner.Scan(_root, new LibraryScanOptions(), _now).Files), scanned.File);
    }

    [PosixFact]
    public void ScanFile_says_when_a_file_still_being_written_will_have_settled()
    {
        var episode = WriteFile("Show/S01E01.mkv", modifiedMinutesAgo: 0);
        var options = new LibraryScanOptions();

        var settling = Assert.IsType<SingleFileScan.Settling>(_scanner.ScanFile(_root, episode, options, _now));

        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(episode), TimeSpan.Zero) + options.SettlingPeriod, settling.SettlesAt);
    }

    [Fact]
    public void ScanFile_reports_a_missing_file()
    {
        Assert.IsType<SingleFileScan.Missing>(
            _scanner.ScanFile(_root, Path.Combine(_root, "Show", "gone.mkv"), new LibraryScanOptions(), _now));
    }

    [PosixFact]
    public void ScanFile_skips_a_file_the_library_does_not_hold()
    {
        var subtitle = WriteFile("Show/S01E01.srt", modifiedMinutesAgo: 30);
        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(MediaType.Tv) };

        Assert.IsType<SingleFileScan.NotMedia>(_scanner.ScanFile(_root, subtitle, options, _now));
    }

    [Fact]
    public void ScanFile_refuses_a_file_outside_the_library()
    {
        using var outside = new TemporaryDirectory();
        var stray = System.IO.Path.Combine(outside.Path, "stray.mkv");
        File.WriteAllText(stray, "x");
        File.SetLastWriteTimeUtc(stray, _now.UtcDateTime.AddDays(-1));

        Assert.IsType<SingleFileScan.Refused>(_scanner.ScanFile(_root, stray, new LibraryScanOptions(), _now));
    }

    [Fact]
    public void ScanFile_refuses_a_copy_waiting_in_duplicate_quarantine()
    {
        var quarantined = WriteFile(Optimisarr.Core.Deduplication.DedupeQuarantinePaths.FolderName + "/action/Copy.mkv", modifiedMinutesAgo: 60);

        Assert.IsType<SingleFileScan.Refused>(_scanner.ScanFile(_root, quarantined, new LibraryScanOptions(), _now));
    }

    [PosixFact]
    public void ScanFile_refuses_a_file_reached_through_a_link()
    {
        using var outside = new TemporaryDirectory();
        var real = System.IO.Path.Combine(outside.Path, "real.mkv");
        File.WriteAllText(real, "x");
        File.SetLastWriteTimeUtc(real, _now.UtcDateTime.AddDays(-1));
        var link = System.IO.Path.Combine(_root, "linked.mkv");
        File.CreateSymbolicLink(link, real);

        Assert.IsType<SingleFileScan.Refused>(_scanner.ScanFile(_root, link, new LibraryScanOptions(), _now));
    }

    [PosixFact]
    public void ScanFile_records_the_spelling_on_disk_when_the_manager_reports_another_letter_case()
    {
        // A manager on a case-insensitive mount can name a file in any case. Taking its spelling as the
        // inventory path would give one file two rows, so the name the folder listing holds is recorded.
        var episode = WriteFile("Show/Season 01/S01E01.mkv", modifiedMinutesAgo: 30);

        var scanned = Assert.IsType<SingleFileScan.Settled>(
            _scanner.ScanFile(_root, Path.Combine(_root, "show", "SEASON 01", "s01e01.MKV"), new LibraryScanOptions(), _now));

        Assert.Equal(episode, scanned.File.AbsolutePath);
        Assert.Equal(Path.Combine("Show", "Season 01", "S01E01.mkv"), scanned.File.RelativePath);
    }

    [PosixFact]
    public void The_on_disk_spelling_prefers_an_exact_name_and_refuses_to_guess_between_case_twins()
    {
        // Simulates a case-sensitive folder holding two names that differ only in case.
        IEnumerable<string> Listing(string folder) => folder == "/lib" ? ["Show"] : ["ep.mkv", "EP.mkv"];

        Assert.Equal("/lib/Show/EP.mkv", LibraryPathSpelling.OnDisk("/lib", "/lib/show/EP.mkv", Listing));
        Assert.Null(LibraryPathSpelling.OnDisk("/lib", "/lib/Show/Ep.mkv", Listing));
        Assert.Null(LibraryPathSpelling.OnDisk("/lib", "/lib/Show/gone.mkv", Listing));
        Assert.Null(LibraryPathSpelling.OnDisk("/lib", "/elsewhere/Show/EP.mkv", Listing));
    }

    [PosixFact]
    public void The_on_disk_spelling_takes_the_listing_name_for_a_case_variant()
    {
        IEnumerable<string> Listing(string folder) => folder == "/lib" ? ["Show"] : ["Episode One.mkv"];

        Assert.Equal("/lib/Show/Episode One.mkv", LibraryPathSpelling.OnDisk("/lib", "/lib/SHOW/episode one.MKV", Listing));
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
