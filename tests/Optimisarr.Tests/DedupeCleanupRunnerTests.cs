using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>A real scan of real files, then a real cleanup of what it found.</summary>
public sealed class DedupeCleanupRunnerTests : IAsyncLifetime
{
    private const string Radarr = "http://radarr:7878";
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("optimisarr-cleanup-").FullName);
    private readonly Dictionary<string, string> _answers = [];
    // An answer that replaces the usual one once a path has been asked for this many times.
    private readonly Dictionary<string, (int After, string Body)> _answersAfter = [];
    private ServiceProvider _services = null!;
    private ExactDuplicateCoordinator _scans = null!;
    private int _libraryId;

    private string Library => Path.Combine(_root, "Film");
    private DedupeCleanupRunner Runner => _services.GetRequiredService<DedupeCleanupRunner>();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Library);
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "cleanup.db")};Pooling=False"));
        services.AddSingleton<FileOperationClaims>();
        services.AddSingleton<DedupeJournalStore>();
        services.AddSingleton<DedupeQuarantineService>();
        services.AddSingleton<ExactDuplicateStore>();
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(_answers, _answersAfter)));
        services.AddScoped(provider => new ArrTrackedFilesService(provider.GetRequiredService<OptimisarrDbContext>(),
            provider.GetRequiredService<IHttpClientFactory>(), NullLogger<ArrTrackedFilesService>.Instance));
        services.AddSingleton(provider => new DedupeCleanupRunner(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ExactDuplicateStore>(), provider.GetRequiredService<DedupeJournalStore>(),
            provider.GetRequiredService<DedupeQuarantineService>(), new Lifetime(),
            NullLogger<DedupeCleanupRunner>.Instance, TimeSpan.Zero));
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        var library = new Library { Name = "Film", Path = Library, MediaType = MediaType.Film };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _libraryId = library.Id;
        _scans = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.Zero), _services.GetRequiredService<ExactDuplicateStore>());
        await _scans.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _scans.StopAsync(default);
        _scans.Dispose();
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private async Task<string> AddAsync(string relative, byte[] content)
    {
        var path = Path.Combine(Library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        await RegisterAsync(relative, path);
        return path;
    }

    private async Task RegisterAsync(string relative, string path)
    {
        var info = new FileInfo(path);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.MediaFiles.Add(new MediaFile
        {
            LibraryId = _libraryId, Path = path, RelativePath = relative, SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Status = MediaFileStatus.Probed,
        });
        await db.SaveChangesAsync();
    }

    private async Task ScanAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var inputs = await ExactDuplicateInputs.ReadAsync(db, _libraryId, default);
        Assert.True(await _scans.TryStartAsync(_libraryId, Library, inputs, false, default, [new ExactDuplicateLibrary(_libraryId, "Film", Library, "Film")]));
        Assert.True(await TestWait.UntilAsync(async () => (await _scans.ReadAsync(_libraryId, 0, default)).Status == "Completed"));
    }

    private async Task ConnectRadarrAsync(params string[] tracked)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.ArrConnections.Add(new ArrConnection { Name = "Radarr", Type = ArrConnectionType.Radarr, BaseUrl = Radarr, ApiKey = "key" });
        await db.SaveChangesAsync();
        _answers[Radarr + "/api/v3/system/status"] = """{ "appName": "Radarr", "version": "5.0" }""";
        var movies = tracked.Select((path, index) => $$"""{ "id": {{index + 1}}, "hasFile": true, "movieFile": { "path": "{{path}}" } }""");
        _answers[Radarr + "/api/v3/movie"] = "[" + string.Join(",", movies) + "]";
    }

    private async Task<DedupeCleanupStatus> CleanAsync()
    {
        Assert.Equal(DedupeCleanupStart.Started, await Runner.TryStartAsync(_libraryId, default));
        await Runner.Completion;
        return Runner.Status(_libraryId);
    }

    private string Quarantined(string name) => Directory
        .EnumerateFiles(Path.Combine(Library, DedupeQuarantinePaths.FolderName), name, SearchOption.AllDirectories).Single();

    [PosixFact]
    public async Task With_no_manager_the_spare_moves_to_quarantine_and_one_copy_stays()
    {
        var keeper = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();

        var preview = await Runner.PreviewAsync(_libraryId, default);
        Assert.Equal((true, 1, 1, 4L), (preview.Ready, preview.Groups, preview.Copies, preview.BytesToFree));

        var status = await CleanAsync();

        Assert.Equal((1, 1, 4L), (status.Done, status.Moved, status.BytesHeld));
        Assert.Empty(status.Failures);
        Assert.False(status.Running);
        Assert.True(File.Exists(keeper));
        Assert.False(File.Exists(spare));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Quarantined("Title.mkv")));
    }

    [PosixFact]
    public async Task The_copy_a_manager_tracks_stays_though_the_rule_would_otherwise_keep_the_other()
    {
        var spare = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var tracked = await AddAsync("Movies/Film (2020)/Title.mkv", [1, 2, 3, 4]);
        await ConnectRadarrAsync("/movies/Movies/Film (2020)/Title.mkv", "/movies/Movies/Other (2021)/Other.mkv");
        await AddAsync("Movies/Other (2021)/Other.mkv", [9]);
        await ScanAsync();

        var status = await CleanAsync();

        Assert.Equal(1, status.Moved);
        Assert.True(File.Exists(tracked));
        Assert.False(File.Exists(spare));
    }

    [PosixFact]
    public async Task A_manager_that_cannot_answer_moves_nothing_and_says_why()
    {
        var first = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var second = await AddAsync("B/Title.mkv", [1, 2, 3, 4]);
        await ConnectRadarrAsync();
        _answers.Remove(Radarr + "/api/v3/movie");
        await ScanAsync();

        var preview = await Runner.PreviewAsync(_libraryId, default);

        Assert.Equal(0, preview.Groups);
        Assert.Equal(1, preview.LeftAlone[nameof(DedupeCleanupRefusal.ManagerUnavailable)]);
        Assert.Equal(DedupeCleanupStart.NothingToDo, await Runner.TryStartAsync(_libraryId, default));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [PosixFact]
    public async Task A_report_that_no_longer_matches_the_inventory_cannot_start()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("B/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        await AddAsync("C/Title.mkv", [1, 2, 3, 4]);

        Assert.False((await Runner.PreviewAsync(_libraryId, default)).Ready);
        Assert.Equal(DedupeCleanupStart.NotReady, await Runner.TryStartAsync(_libraryId, default));
    }

    [PosixFact]
    public async Task A_file_changed_since_the_scan_is_reported_and_stays_and_its_files_are_released()
    {
        var first = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var second = await AddAsync("B/Deep/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        var modified = File.GetLastWriteTimeUtc(second);
        await File.WriteAllBytesAsync(second, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(second, modified);

        var status = await CleanAsync();

        Assert.Equal(0, status.Moved);
        Assert.Single(status.Failures);
        Assert.Equal([9, 9, 9, 9], await File.ReadAllBytesAsync(second));
        Assert.True(File.Exists(first));
        await using var scope = _services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeFileClaims.AsNoTracking().ToListAsync());
    }

    [PosixFact]
    public async Task A_manager_that_starts_tracking_the_spare_during_the_run_stops_the_move()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deep/Title.mkv", [1, 2, 3, 4]);
        await ConnectRadarrAsync();
        await ScanAsync();
        _answersAfter["/api/v3/movie"] = (1, """[{ "id": 1, "hasFile": true, "movieFile": { "path": "/movies/B/Deep/Title.mkv" } }]""");

        var status = await CleanAsync();

        Assert.Equal(0, status.Moved);
        Assert.Contains("now tracks this copy", Assert.Single(status.Failures).Reason);
        Assert.True(File.Exists(spare));
    }

    [PosixFact]
    public async Task A_manager_tracking_another_name_of_a_hard_linked_file_keeps_it_and_the_real_spare_moves()
    {
        var keeper = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var linked = Path.Combine(Library, "Z", "Title.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        using (var link = System.Diagnostics.Process.Start("ln", [keeper, linked])) await link.WaitForExitAsync();
        await RegisterAsync("Z/Title.mkv", linked);
        var spare = await AddAsync("B/Title.mkv", [1, 2, 3, 4]);
        await ConnectRadarrAsync("/movies/Z/Title.mkv", "/movies/K/K.mkv");
        await ScanAsync();

        var status = await CleanAsync();

        Assert.Equal(1, status.Moved);
        Assert.Empty(status.Failures);
        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(linked));
        Assert.False(File.Exists(spare));
    }

    [PosixFact]
    public async Task A_copy_deleted_since_the_scan_makes_the_report_out_of_date()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        File.Delete(spare);

        Assert.False((await Runner.PreviewAsync(_libraryId, default)).Ready);
        Assert.Equal(DedupeCleanupStart.NotReady, await Runner.TryStartAsync(_libraryId, default));
    }

    [PosixFact]
    public async Task Stopping_a_run_before_the_move_releases_the_files()
    {
        await AddAsync("A/Title.mkv", new byte[4_000_000]);
        var spare = await AddAsync("B/Title.mkv", new byte[4_000_000]);
        await ScanAsync();

        Assert.Equal(DedupeCleanupStart.Started, await Runner.TryStartAsync(_libraryId, default));
        Runner.Cancel(_libraryId);
        await Runner.Completion;

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        if (File.Exists(spare)) Assert.Empty(await db.DedupeFileClaims.AsNoTracking().ToListAsync());
    }

    private static readonly DateTimeOffset Tonight = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    private readonly SettableClock _clock = new(Tonight);

    private AutoDedupeService Automatic => AutomaticWith(_scans);

    private AutoDedupeService AutomaticWith(ExactDuplicateCoordinator scans, AutoDedupeTimings? timings = null) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), scans, Runner, _clock,
            NullLogger<AutoDedupeService>.Instance, timings ?? new AutoDedupeTimings { Poll = TimeSpan.FromMilliseconds(10) });

    private async Task RunAtAsync(DateTimeOffset now)
    {
        _clock.Now = now;
        await Automatic.RunDueAsync(default);
    }

    private async Task<Library> LibraryAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Libraries.AsNoTracking().SingleAsync(l => l.Id == _libraryId);
    }

    private async Task ChangeLibraryAsync(Action<Library> change)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        change(await db.Libraries.SingleAsync(l => l.Id == _libraryId));
        await db.SaveChangesAsync();
    }

    [PosixFact]
    public async Task The_daily_cleanup_scans_and_moves_the_spare_and_says_what_it_did()
    {
        var keeper = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);

        await RunAtAsync(Tonight);

        Assert.True(File.Exists(keeper));
        Assert.False(File.Exists(spare));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Quarantined("Title.mkv")));
        var library = await LibraryAsync();
        Assert.Equal((Tonight, AutoDedupeOutcome.Cleaned, 1, 4L),
            (library.LastAutoDedupeAt, library.LastAutoDedupeOutcome, library.LastAutoDedupeMoved, library.LastAutoDedupeBytes));
    }

    [PosixFact]
    public async Task The_daily_cleanup_runs_once_a_day()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        await RunAtAsync(Tonight);
        Assert.Equal(AutoDedupeOutcome.NothingToClean, (await LibraryAsync()).LastAutoDedupeOutcome);
        var spare = await AddAsync("B/Title.mkv", [1, 2, 3, 4]);

        await RunAtAsync(Tonight.AddHours(12));
        Assert.True(File.Exists(spare));

        await RunAtAsync(Tonight.AddDays(1));
        Assert.False(File.Exists(spare));
    }

    [PosixFact]
    public async Task A_library_that_did_not_ask_is_never_cleaned()
    {
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);

        await RunAtAsync(Tonight);

        Assert.True(File.Exists(spare));
        Assert.Null((await LibraryAsync()).LastAutoDedupeAt);
    }

    [PosixFact]
    public async Task Dry_run_mode_moves_nothing_and_says_so()
    {
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.DryRunMode, Value = "True" });
            await db.SaveChangesAsync();
        }

        await RunAtAsync(Tonight);

        Assert.True(File.Exists(spare));
        Assert.Equal(AutoDedupeOutcome.DryRun, (await LibraryAsync()).LastAutoDedupeOutcome);
    }

    [PosixFact]
    public async Task A_manager_that_cannot_answer_leaves_every_copy_in_place()
    {
        var keeper = await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.ArrConnections.Add(new ArrConnection { Name = "Radarr", Type = ArrConnectionType.Radarr, BaseUrl = Radarr, ApiKey = "key" });
            await db.SaveChangesAsync();
        }

        await RunAtAsync(Tonight);

        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(spare));
        Assert.Equal(AutoDedupeOutcome.ManagerUnavailable, (await LibraryAsync()).LastAutoDedupeOutcome);
    }

    private async Task<int> AddLibraryAsync(string name, bool autoDedupe = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = name, Path = Path.Combine(_root, name), MediaType = MediaType.Film, AutoDedupeEnabled = autoDedupe };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    private async Task<string> AddToAsync(int libraryId, string libraryName, string relative, byte[] content)
    {
        var path = Path.Combine(_root, libraryName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        var info = new FileInfo(path);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.MediaFiles.Add(new MediaFile
        {
            LibraryId = libraryId, Path = path, RelativePath = relative, SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Status = MediaFileStatus.Probed,
        });
        await db.SaveChangesAsync();
        return path;
    }

    private async Task ScanTogetherAsync(params int[] others)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        int[] ids = [_libraryId, .. others];
        var inputs = await ExactDuplicateInputs.ReadAsync(db, ids, default);
        var selected = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).OrderBy(l => l.Id)
            .Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync();
        Assert.True(await _scans.TryStartAsync(_libraryId, Library, inputs, false, default, selected));
        Assert.True(await TestWait.UntilAsync(async () => (await _scans.ReadAsync(_libraryId, 0, default)).Status == "Completed"));
    }

    [PosixFact]
    public async Task The_daily_cleanup_moves_only_copies_inside_the_library_that_asked()
    {
        var other = await AddLibraryAsync("Other");
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var ownSpare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await AddToAsync(other, "Other", "C/Clip.mkv", [5, 6, 7, 8]);
        var otherSpare = await AddToAsync(other, "Other", "D/Deeper/Clip.mkv", [5, 6, 7, 8]);
        await ScanTogetherAsync(other);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);

        await RunAtAsync(Tonight);

        Assert.False(File.Exists(ownSpare));
        Assert.True(File.Exists(otherSpare));
        Assert.Equal(1, (await LibraryAsync()).LastAutoDedupeMoved);
    }

    [PosixFact]
    public async Task The_daily_cleanup_leaves_content_someone_decided_about_in_another_report()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.ExactDuplicateReviewDecisions.Add(new ExactDuplicateReviewDecision
            {
                LibraryId = _libraryId, ScopeKey = new string('c', 64), ReviewKey = new string('d', 64), Choice = "Ignore", KeepIds = "[]",
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4 })), SizeBytes = 4,
                SavedAt = Tonight,
            });
            await db.SaveChangesAsync();
        }

        await RunAtAsync(Tonight);

        Assert.True(File.Exists(spare));
        Assert.Equal(AutoDedupeOutcome.NothingToClean, (await LibraryAsync()).LastAutoDedupeOutcome);
    }

    [PosixFact]
    public async Task An_automatic_cleanup_stops_before_the_next_group_once_it_is_no_longer_allowed()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("A/Second.mkv", [5, 6, 7, 8]);
        await AddAsync("B/Deeper/Second.mkv", [5, 6, 7, 8]);
        await ScanAsync();
        long scanId;
        await using (var scope = _services.CreateAsyncScope())
            scanId = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().ExactDuplicateScans.Where(s => s.LibraryId == _libraryId).Select(s => s.Id).SingleAsync();
        var checks = 0;

        // Allowed when it starts and for the first group, then turned off.
        var launch = await Runner.StartAsync(_libraryId, default, new DedupeCleanupLimits(scanId, _libraryId, _ => Task.FromResult(++checks <= 2)));
        var status = await launch.Finished!;

        Assert.Equal(1, status.Moved);
        Assert.True(status.Interrupted);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Library, DedupeQuarantinePaths.FolderName), "*.mkv", SearchOption.AllDirectories));
    }

    [PosixFact]
    public async Task An_automatic_cleanup_that_is_not_allowed_at_the_start_says_so_and_moves_nothing()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        long scanId;
        await using (var scope = _services.CreateAsyncScope())
            scanId = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().ExactDuplicateScans.Where(s => s.LibraryId == _libraryId).Select(s => s.Id).SingleAsync();

        var launch = await Runner.StartAsync(_libraryId, default, new DedupeCleanupLimits(scanId, _libraryId, _ => Task.FromResult(false)));

        Assert.Equal(DedupeCleanupStart.NotAllowed, launch.Start);
        Assert.True(File.Exists(spare));
    }

    [PosixFact]
    public async Task An_automatic_cleanup_refuses_a_report_other_than_its_own()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();

        var launch = await Runner.StartAsync(_libraryId, default, new DedupeCleanupLimits(-1, _libraryId, _ => Task.FromResult(true)));

        Assert.Equal(DedupeCleanupStart.NotReady, launch.Start);
        Assert.True(File.Exists(spare));
    }

    [PosixFact]
    public async Task An_automatic_scan_does_not_replace_a_report_made_after_it_looked()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var inputs = await ExactDuplicateInputs.ReadAsync(db, _libraryId, default);

        Assert.Null(await _scans.TryStartScanAsync(_libraryId, Library, inputs, false, default, null, new ExactDuplicateScanExpectation(null)));
    }

    [PosixFact]
    public async Task A_scan_stuck_in_the_queue_is_withdrawn_and_recorded_so_later_libraries_still_run()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await AddAsync("B/Deeper/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        var later = await AddLibraryAsync("Later", autoDedupe: true);
        // Never started, so every scan it accepts stays queued.
        var stuck = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.Zero), _services.GetRequiredService<ExactDuplicateStore>());

        await AutomaticWith(stuck, new AutoDedupeTimings { Poll = TimeSpan.FromMilliseconds(10), QueuedLimit = TimeSpan.Zero }).RunDueAsync(default);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Equal(AutoDedupeOutcome.ScanFailed, (await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == _libraryId)).LastAutoDedupeOutcome);
        Assert.Equal(AutoDedupeOutcome.ScanFailed, (await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == later)).LastAutoDedupeOutcome);
        Assert.False(await db.ExactDuplicateScans.AnyAsync(s => s.ActiveSlot != null));
        stuck.Dispose();
    }

    [PosixFact]
    public async Task A_withdrawn_scan_is_skipped_when_the_executor_reaches_it()
    {
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var inputs = await ExactDuplicateInputs.ReadAsync(db, _libraryId, default);
        var late = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.Zero), _services.GetRequiredService<ExactDuplicateStore>());
        var withdrawn = await late.TryStartScanAsync(_libraryId, Library, inputs, false, default);
        Assert.True(await late.WithdrawQueuedAsync(withdrawn!.Value, default));

        var next = await late.TryStartScanAsync(_libraryId, Library, inputs, false, default);
        await late.StartAsync(default);

        Assert.NotNull(next);
        Assert.True(await TestWait.UntilAsync(async () => await late.ReadStateAsync(next!.Value, default) == ExactDuplicateScanState.Completed));
        await late.StopAsync(default);
        late.Dispose();
    }

    [PosixFact]
    public async Task Each_library_records_when_its_own_run_started()
    {
        var other = await AddLibraryAsync("Other", autoDedupe: true);
        await AddAsync("A/Title.mkv", [1, 2, 3, 4]);
        await ChangeLibraryAsync(l => l.AutoDedupeEnabled = true);
        _clock.Step = TimeSpan.FromMinutes(1);

        await AutomaticWith(_scans, new AutoDedupeTimings { Poll = TimeSpan.FromMilliseconds(10), StalledLimit = TimeSpan.FromDays(365), QueuedLimit = TimeSpan.FromDays(365) })
            .RunDueAsync(default);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var first = (await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == _libraryId)).LastAutoDedupeAt;
        var second = (await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == other)).LastAutoDedupeAt;
        Assert.True(second > first);
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var now = Now;
            Now += Step;
            return now;
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class Handler(Dictionary<string, string> answers, Dictionary<string, (int After, string Body)> later) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            _asked[url.AbsolutePath] = _asked.GetValueOrDefault(url.AbsolutePath) + 1;
            var body = later.TryGetValue(url.AbsolutePath, out var next) && _asked[url.AbsolutePath] > next.After
                ? next.Body
                : answers.GetValueOrDefault(url.ToString());
            return Task.FromResult(body is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
