using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// The dispatcher writes a converted M4A's tags between its encode and its verification, so the
/// file verified is the file kept (#593).
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class Mp4TagDispatchTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private sealed record CarryOver(string SourcePath, string OutputPath, IReadOnlyDictionary<string, string>? SourceTags,
        IReadOnlyDictionary<string, string>? OutputTags, string? SourceLanguage, string Marker);

    private sealed class RecordingTags : MusicTagService
    {
        public List<CarryOver> Calls { get; } = [];

        public override Task<string?> CarryOverMp4TagsAsync(string sourcePath, string outputPath,
            IReadOnlyDictionary<string, string>? sourceTags, IReadOnlyDictionary<string, string>? outputTags,
            string? sourceLanguage, string marker, CancellationToken cancellationToken)
        {
            Calls.Add(new(sourcePath, outputPath, sourceTags, outputTags, sourceLanguage, marker));
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Probe : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(MediaProbeService.Parse(path.EndsWith(".flac", StringComparison.Ordinal)
                ? """{"format":{"format_name":"flac","tags":{"TITLE":"Title","LANGUAGE":"eng"}},"streams":[{"codec_type":"audio","codec_name":"flac"}]}"""
                : """{"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","tags":{"title":"Title"}},"streams":[{"codec_type":"audio","codec_name":"aac","tags":{"language":"und"}}]}"""));
    }

    private async Task<List<CarryOver>> PrepareAsync(string outputExtension)
    {
        var tags = new RecordingTags();
        var services = new ServiceCollection();
        services.AddSingleton<IMediaProbeService>(new Probe());
        services.AddSingleton<MusicTagService>(tags);
        await using var isolated = services.BuildServiceProvider();
        using var dispatcher = ActivatorUtilities.CreateInstance<QueueDispatcher>(api.Services,
            isolated.GetRequiredService<IServiceScopeFactory>());
        var spec = new TranscodeSpec("/music/Track.flac", "/work/7/Track" + outputExtension, null, null, null, false,
            Kind: MediaKind.Audio, AudioEncoder: outputExtension == ".m4a" ? "aac" : "libopus", AudioBitrateKbps: 128);
        var work = new QueueDispatcher.JobWork(spec, FfmpegCommandBuilder.Build(spec), null, null, false, false, null, null, null,
            false, null, false, 0, new OriginalSnapshot("/music/Track.flac", 1000, 100, 1, 0, false, false, MediaKind.Audio),
            VerificationPolicy.Default, VideoQualityStrategy.Fixed, WorkPlacement.LocalOnly, null, false, 0, false, false);

        await dispatcher.PrepareOutputForVerificationAsync(7, work, default);
        return tags.Calls;
    }

    [Fact]
    public async Task An_aac_m4a_has_its_tags_and_marker_written_before_it_is_verified()
    {
        var call = Assert.Single(await PrepareAsync(".m4a"));

        Assert.Equal("/music/Track.flac", call.SourcePath);
        Assert.Equal("/work/7/Track.m4a", call.OutputPath);
        Assert.Equal("Title", call.SourceTags!["TITLE"]);
        Assert.Equal("und", call.OutputTags!["language"]);
        Assert.Equal("eng", call.SourceLanguage);
        Assert.False(string.IsNullOrWhiteSpace(call.Marker));
    }

    [Fact]
    public async Task Other_music_targets_keep_the_tags_ffmpeg_wrote() =>
        Assert.Empty(await PrepareAsync(".opus"));
}
