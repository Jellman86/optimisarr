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
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(_answers)));
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
        var info = new FileInfo(path);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.MediaFiles.Add(new MediaFile
        {
            LibraryId = _libraryId, Path = path, RelativePath = relative, SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Status = MediaFileStatus.Probed,
        });
        await db.SaveChangesAsync();
        return path;
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
        var keeper = await AddAsync("A/Film.mkv", [1, 2, 3, 4]);
        var spare = await AddAsync("B/Deeper/Film.mkv", [1, 2, 3, 4]);
        await ScanAsync();

        var preview = await Runner.PreviewAsync(_libraryId, default);
        Assert.Equal((true, 1, 1, 4L), (preview.Ready, preview.Groups, preview.Copies, preview.BytesToFree));

        var status = await CleanAsync();

        Assert.Equal((1, 1, 4L), (status.Done, status.Moved, status.BytesHeld));
        Assert.Empty(status.Failures);
        Assert.False(status.Running);
        Assert.True(File.Exists(keeper));
        Assert.False(File.Exists(spare));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Quarantined("Film.mkv")));
    }

    [PosixFact]
    public async Task The_copy_a_manager_tracks_stays_though_the_rule_would_otherwise_keep_the_other()
    {
        var spare = await AddAsync("A/Film.mkv", [1, 2, 3, 4]);
        var tracked = await AddAsync("Movies/Film (2020)/Film.mkv", [1, 2, 3, 4]);
        await ConnectRadarrAsync("/movies/Movies/Film (2020)/Film.mkv", "/movies/Movies/Other (2021)/Other.mkv");
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
        var first = await AddAsync("A/Film.mkv", [1, 2, 3, 4]);
        var second = await AddAsync("B/Film.mkv", [1, 2, 3, 4]);
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
        await AddAsync("A/Film.mkv", [1, 2, 3, 4]);
        await AddAsync("B/Film.mkv", [1, 2, 3, 4]);
        await ScanAsync();
        await AddAsync("C/Film.mkv", [1, 2, 3, 4]);

        Assert.False((await Runner.PreviewAsync(_libraryId, default)).Ready);
        Assert.Equal(DedupeCleanupStart.NotReady, await Runner.TryStartAsync(_libraryId, default));
    }

    [PosixFact]
    public async Task A_file_changed_since_the_scan_is_reported_and_stays_and_its_files_are_released()
    {
        var first = await AddAsync("A/Film.mkv", [1, 2, 3, 4]);
        var second = await AddAsync("B/Deep/Film.mkv", [1, 2, 3, 4]);
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

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class Handler(Dictionary<string, string> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answers.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
