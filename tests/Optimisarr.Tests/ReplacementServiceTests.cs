using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ReplacementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _workDir;
    private readonly string _trashDir;

    public ReplacementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();

        _root = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "data");
        _workDir = Path.Combine(_root, "work");
        _trashDir = Path.Combine(_root, "trash");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_workDir);
    }

    [Fact]
    public async Task Replace_quarantines_the_original_and_puts_the_verified_output_in_place()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL-DATA", "NEW-SMALLER");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);

        var finalPath = Path.Combine(_dataDir, "Movie.mkv");
        Assert.True(File.Exists(finalPath));
        Assert.Equal("NEW-SMALLER", File.ReadAllText(finalPath));
        Assert.False(File.Exists(originalPath));   // original moved out of place
        Assert.False(File.Exists(outputPath));     // output moved out of the work dir

        var replacement = result.Replacement!;
        Assert.True(File.Exists(replacement.QuarantinePath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(replacement.QuarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, replacement.Status);

        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        var media = await db.MediaFiles.SingleAsync();
        Assert.Equal(finalPath, media.Path);
        Assert.Equal("NEW-SMALLER".Length, media.SizeBytes);
    }

    [Fact]
    public async Task Replace_handles_an_unchanged_container_by_replacing_in_place()
    {
        var (originalPath, outputPath) = WriteFiles("Show.mkv", "Show.mkv", "OLD", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.Equal(originalPath, result.Replacement!.FinalPath);
        Assert.Equal("NEW", File.ReadAllText(originalPath));
        Assert.Equal("OLD", File.ReadAllText(result.Replacement.QuarantinePath));
    }

    [Fact]
    public async Task Replace_refuses_a_job_that_has_not_passed_verification()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: false);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.True(File.Exists(originalPath));         // nothing touched
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_refuses_cross_filesystem_moves_when_policy_disallows_them()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId, canMoveAtomically: (_, _) => false);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.Contains("cross-filesystem", result.Message);
        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(outputPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
    }

    [Fact]
    public async Task Replace_allows_cross_filesystem_moves_when_policy_allows_them()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        await SetCrossFilesystemReplacementAsync(allowed: true);

        var result = await ReplaceAsync(jobId, canMoveAtomically: (_, _) => false);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.False(File.Exists(originalPath));
        Assert.Equal("NEW", File.ReadAllText(result.Replacement!.FinalPath));
    }

    [Fact]
    public async Task Rollback_restores_the_original_and_removes_the_replacement_output()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL-DATA", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var finalPath = replaced.Replacement!.FinalPath;

        var result = await RollbackAsync(replaced.Replacement.Id);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(originalPath));
        Assert.False(File.Exists(finalPath));   // the transcoded output is gone

        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.RolledBack, replacement.Status);
        Assert.NotNull(replacement.RolledBackAt);
        var media = await db.MediaFiles.SingleAsync();
        Assert.Equal(originalPath, media.Path);
        Assert.Equal("ORIGINAL-DATA".Length, media.SizeBytes);
    }

    [Fact]
    public async Task Rollback_refuses_an_already_rolled_back_replacement()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        await RollbackAsync(replaced.Replacement!.Id);

        var second = await RollbackAsync(replaced.Replacement.Id);

        Assert.Equal(ReplacementResultKind.Invalid, second.Kind);
    }

    private (string OriginalPath, string OutputPath) WriteFiles(
        string originalName, string outputName, string originalContent, string outputContent)
    {
        var originalPath = Path.Combine(_dataDir, originalName);
        var outputPath = Path.Combine(_workDir, outputName);
        File.WriteAllText(originalPath, originalContent);
        File.WriteAllText(outputPath, outputContent);
        return (originalPath, outputPath);
    }

    private async Task<int> SeedReadyJobAsync(string originalPath, string outputPath, bool verificationPassed)
    {
        await using var db = new OptimisarrDbContext(_options);
        var media = new MediaFile
        {
            Path = originalPath,
            RelativePath = Path.GetFileName(originalPath),
            SizeBytes = new FileInfo(originalPath).Length,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var job = new Job
        {
            MediaFileId = media.Id,
            Status = JobStatus.ReadyToReplace,
            WorkOutputPath = outputPath,
            VerificationPassed = verificationPassed,
            VerifiedAt = DateTimeOffset.UtcNow,
            Progress = 1.0
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<ReplacementActionResult> ReplaceAsync(
        int jobId,
        Func<string, string, bool>? canMoveAtomically = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db, canMoveAtomically);
        return await service.ReplaceAsync(jobId, CancellationToken.None);
    }

    private async Task<ReplacementActionResult> RollbackAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db);
        return await service.RollbackAsync(replacementId, CancellationToken.None);
    }

    private ReplacementService NewService(
        OptimisarrDbContext db,
        Func<string, string, bool>? canMoveAtomically = null)
    {
        var inventory = new LibraryInventoryService(db, new LibraryScanner(), new MediaProbeService(), new ImageMarkerService());
        return new ReplacementService(
            db,
            inventory,
            new SettingsStore(db),
            _trashDir,
            NullLogger<ReplacementService>.Instance,
            canMoveAtomically);
    }

    private async Task SetCrossFilesystemReplacementAsync(bool allowed)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(current with { ReplacementAllowCrossFilesystem = allowed }, CancellationToken.None);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
