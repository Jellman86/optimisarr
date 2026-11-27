using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using static Optimisarr.Tests.Mp4TagFixtures;

namespace Optimisarr.Tests;

/// <summary>
/// A converted M4A has its marker and the tags FFmpeg dropped written into it on this server, and is
/// verified from its own atoms (#593).
/// </summary>
public sealed class Mp4TagWiringTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-mp4-tags-").FullName;

    private static readonly Dictionary<string, string> FfmpegTags = new()
    {
        ["major_brand"] = "M4A ", ["title"] = "Title", ["encoder"] = "Lavf61.7.103",
        ["language"] = "und", ["handler_name"] = "SoundHandler", ["vendor_id"] = "[0][0][0][0]",
    };

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Mp4Tags Tags(string path) =>
        Mp4TagReader.Read(new MusicTagBytes(File.ReadAllBytes(path))).Tags ?? throw new Xunit.Sdk.XunitException("unreadable");

    [Fact]
    public async Task The_marker_and_the_dropped_tags_are_written_into_the_new_file_and_nothing_else_changes()
    {
        var source = Write("Track.flac", "fLaC"u8.ToArray());
        var encoded = M4a(Text("©nam", "Title"), Text("©too", "Lavf61.7.103"));
        var output = Write("Track.m4a", encoded);

        var problem = await new MusicTagService().CarryOverMp4TagsAsync(source, output,
            new Dictionary<string, string> { ["TITLE"] = "Title", ["MUSICBRAINZ_ALBUMID"] = "abc", ["LANGUAGE"] = "eng" }, FfmpegTags, "eng", "1.2.3", default);

        Assert.Null(problem);
        var written = File.ReadAllBytes(output);
        var moov = (int)Tags(output).MoovOffset;
        Assert.Equal(encoded.AsSpan(0, moov).ToArray(), written.AsSpan(0, moov).ToArray());
        Assert.Equal(["©nam", "©too", "----:com.apple.iTunes:LANGUAGE", "----:com.apple.iTunes:MusicBrainz Album Id", "----:com.optimisarr:optimisarr"],
            Tags(output).Items.Select(item => item.Key));
        Assert.Equal("fLaC"u8.ToArray(), File.ReadAllBytes(source));
    }

    [Fact]
    public async Task A_file_whose_movie_box_comes_before_its_samples_is_left_as_it_is()
    {
        var bytes = (byte[])[.. Ftyp, .. Moov(Box("udta", ItunesMeta(Text("©nam", "Title")))), .. Mdat];
        var output = Write("Track.m4a", bytes);

        var problem = await new MusicTagService().CarryOverMp4TagsAsync(Write("Track.flac", []), output, null, FfmpegTags, null, "1.2.3", default);

        Assert.Contains("not at the end", problem);
        Assert.Equal(bytes, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task A_file_with_quicktime_keys_is_left_as_it_is()
    {
        var bytes = (byte[])[.. Ftyp, .. Mdat, .. Moov(QuickTimeKeysMeta(("TITLE", "Title")))];
        var output = Write("Track.m4a", bytes);

        Assert.NotNull(await new MusicTagService().CarryOverMp4TagsAsync(Write("Track.flac", []), output, null, FfmpegTags, null, "1.2.3", default));
        Assert.Equal(bytes, File.ReadAllBytes(output));
    }

    private VerificationService Service(MusicTagService? musicTags)
    {
        var missing = Path.Combine(_root, "no-media-tool-is-installed");
        return new(new MediaProbeService(missing), new DecodeHealthCheck(missing),
            new TimestampIntegrityCheck(missing), new ReferenceFrameAlignmentProbe(missing),
            new QualityScoreService(missing), new LoudnessService(missing),
            new ImageQualityService(missing), new ImageMetadataService(missing), new TranscodeOptions(missing),
            musicTags: musicTags);
    }

    private async Task<VerificationCheck> MetadataCheck(byte[] output, MusicTagService? musicTags)
    {
        var source = Write("Source.flac", "source"u8.ToArray());
        var candidate = Write("Candidate.m4a", output);
        var report = (await Service(musicTags).VerifyAsync(
            new OriginalSnapshot(source, 6, 16, 1, 0, false, false, MediaKind.Audio), candidate, VerificationPolicy.Default, default)).Report;
        return report.Checks.Single(check => check.Name == "Audio metadata and artwork");
    }

    [Fact]
    public async Task Verification_reads_an_m4a_s_own_atoms_and_refuses_quicktime_keys()
    {
        var keys = await MetadataCheck([.. Ftyp, .. Mdat, .. Moov(QuickTimeKeysMeta(("optimisarr", "1.2.3"), ("TITLE", "Title")))], new MusicTagService());
        var itunes = await MetadataCheck(M4a(Text("©nam", "Title"), Freeform("com.optimisarr", "optimisarr", "1.2.3")), new MusicTagService());

        Assert.Equal(CheckOutcome.Failed, keys.Outcome);
        Assert.Contains("QuickTime", keys.Detail);
        Assert.DoesNotContain("QuickTime", itunes.Detail);
        Assert.DoesNotContain("could not be read", itunes.Detail);
    }

    [Fact]
    public async Task Verification_without_a_tag_reader_fails_an_m4a()
    {
        var check = await MetadataCheck(M4a(Text("©nam", "Title")), musicTags: null);

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("could not be read", check.Detail);
    }

    private static TranscodeSpec Music(string output) =>
        new("/music/Track.flac", output, null, null, null, false, Kind: MediaKind.Audio, AudioEncoder: "aac", AudioBitrateKbps: 128);

    [Fact]
    public void An_m4a_conversion_stays_on_this_server()
    {
        Assert.NotNull(QueueDispatcher.ServerOnlyRefusal(Music("/work/Track.m4a")));
        Assert.Null(QueueDispatcher.ServerOnlyRefusal(Music("/work/Track.opus")));
        Assert.Null(QueueDispatcher.ServerOnlyRefusal(Music("/work/Track.mp3")));
        Assert.Null(QueueDispatcher.ServerOnlyRefusal(Music("/work/Track.mp4") with { Kind = MediaKind.Video }));
    }

    [Theory]
    [InlineData("aac", MediaKind.Audio, true)]
    [InlineData(null, MediaKind.Audio, true)]
    [InlineData("opus", MediaKind.Audio, false)]
    [InlineData("mp3", MediaKind.Audio, false)]
    [InlineData("aac", MediaKind.Video, false)]
    public void A_music_library_converting_to_aac_keeps_its_work_here(string? codec, MediaKind kind, bool keeps) =>
        Assert.Equal(keeps, AudioTarget.KeepsOnServer(codec, kind));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
