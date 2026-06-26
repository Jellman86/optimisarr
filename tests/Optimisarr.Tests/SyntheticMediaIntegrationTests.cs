using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class SyntheticMediaIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-synthetic-").FullName;

    public SyntheticMediaIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Synthetic_video_fixture_flows_from_scan_to_candidates()
    {
        var feature = WriteSparseFile("Feature.mkv", 300L * 1024 * 1024);
        var alreadyHevc = WriteSparseFile("AlreadyHevc.mkv", 300L * 1024 * 1024);

        int libraryId;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library
            {
                Name = "Synthetic films",
                Path = _root,
                MediaType = MediaType.Film,
                RuleProfile = RuleProfile.ConservativeHevc
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            libraryId = library.Id;

            var inventory = new LibraryInventoryService(
                db,
                new LibraryScanner(),
                new MediaProbeService(),
                new ImageMarkerService());
            var scan = await inventory.ScanAsync(library, CancellationToken.None);

            Assert.Equal(2, scan.Discovered);
            Assert.Equal(2, scan.Added);
            Assert.Equal(0, scan.Removed);
        }

        await ApplyProbeAsync(feature, VideoProbeJson("h264"));
        await ApplyProbeAsync(alreadyHevc, VideoProbeJson("hevc"));

        await using var readDb = new OptimisarrDbContext(_options);
        var candidates = await new CandidateService(readDb).EvaluateAsync(libraryId, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        var featureCandidate = candidates.Single(candidate => candidate.RelativePath == "Feature.mkv");
        Assert.True(featureCandidate.Eligible);
        Assert.Equal("h264", featureCandidate.Codec);
        Assert.Equal("ConservativeHevc", featureCandidate.Profile);
        Assert.Contains("h264", featureCandidate.Reason);
        Assert.Contains("hevc", featureCandidate.Reason);

        var skippedCandidate = candidates.Single(candidate => candidate.RelativePath == "AlreadyHevc.mkv");
        Assert.False(skippedCandidate.Eligible);
        Assert.Contains("Already hevc", skippedCandidate.Reason);
    }

    private string WriteSparseFile(string relativePath, long sizeBytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(sizeBytes);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        return path;
    }

    private async Task ApplyProbeAsync(string path, string json)
    {
        var probe = MediaProbeService.Parse(json, Path.GetExtension(path));
        Assert.True(probe.Success);

        await using var db = new OptimisarrDbContext(_options);
        var file = await db.MediaFiles.SingleAsync(media => media.Path == path);
        file.Status = MediaFileStatus.Probed;
        file.MediaKind = probe.MediaKind;
        file.Container = probe.Container;
        file.DurationSeconds = probe.DurationSeconds;
        file.VideoCodec = probe.VideoCodec;
        file.Width = probe.Width;
        file.Height = probe.Height;
        file.FrameCount = probe.FrameCount;
        file.AudioCodecs = probe.AudioCodecs.Count > 0 ? string.Join(", ", probe.AudioCodecs) : null;
        file.AudioTrackCount = probe.AudioTrackCount;
        file.AudioBitrateKbps = probe.AudioBitrateKbps;
        file.SubtitleTrackCount = probe.SubtitleTrackCount;
        file.IsHdr = probe.IsHdr;
        file.OptimisedMarker = probe.OptimisedMarker;
        file.ProbedAt = DateTimeOffset.UtcNow;
        file.ProbeError = null;
        await db.SaveChangesAsync();
    }

    private static string VideoProbeJson(string codec) => $$"""
        {
          "streams": [
            { "codec_type": "video", "codec_name": "{{codec}}", "width": 1920, "height": 1080 },
            { "codec_type": "audio", "codec_name": "aac", "bit_rate": "160000" }
          ],
          "format": { "format_name": "matroska,webm", "duration": "120.000000" }
        }
        """;

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
