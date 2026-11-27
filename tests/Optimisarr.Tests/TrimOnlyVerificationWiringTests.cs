using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A trim-only job is replaced only after its audio and its tags are proved to be the source's own.
/// These hold the wiring to that: the checks are always on a trim-only report, and fail whenever
/// their evidence is not gathered.
/// </summary>
public sealed class TrimOnlyVerificationWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));

    private static readonly AudioTrimPlan Trim = new(121_275, 584_325, 44_100, 16, -60);
    private static readonly AudioTrimOnlyPlan Copy = new(AudioTrimOnlyMethod.StreamCopy, "mp3", null);

    private sealed class SameAudio() : KeptAudioComparisonService("no-ffmpeg")
    {
        public override Task<KeptAudioEvidence> CompareAsync(string sourcePath, string outputPath, AudioTrimOnlyMethod method,
            AudioTrimPlan trim, CancellationToken cancellationToken) =>
            Task.FromResult(new KeptAudioEvidence(method, SourcePacketDigests: ["a", "b", "c"], OutputPacketDigests: ["b"]));
    }

    private sealed class Tags(Func<string, MusicTagRead> read) : MusicTagService
    {
        public override Task<MusicTagRead> ReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(read(path));
    }

    private static MusicTagRead Id3(params MusicTag[] tags) => MusicTagRead.Read(new MusicTagSet(MusicTagLayout.Id3, tags, 4));

    private VerificationService Service(KeptAudioComparisonService? keptAudio, MusicTagService? musicTags)
    {
        var missing = Path.Combine(_root, "no-media-tool-is-installed");
        return new(new MediaProbeService(missing), new DecodeHealthCheck(missing),
            new TimestampIntegrityCheck(missing), new ReferenceFrameAlignmentProbe(missing),
            new QualityScoreService(missing), new LoudnessService(missing),
            new ImageQualityService(missing), new ImageMetadataService(missing), new TranscodeOptions(missing),
            keptAudio: keptAudio, musicTags: musicTags);
    }

    private (OriginalSnapshot Original, string Output) Files()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "Track.mp3");
        var output = Path.Combine(_root, "out", "Track.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(source, "source");
        File.WriteAllText(output, "output");
        return (new OriginalSnapshot(source, 6, 16, 1, 0, false, false, MediaKind.Audio, SilenceTrim: Trim, AudioTrimOnly: Copy), output);
    }

    private static CheckOutcome? Outcome(VerificationReport report, string name) => report.Checks.SingleOrDefault(c => c.Name == name)?.Outcome;

    [Fact]
    public async Task A_trim_only_job_is_judged_on_its_kept_audio_and_on_its_tags_read_from_both_files()
    {
        var (original, output) = Files();
        var read = new List<string>();
        var tags = new Tags(path => { read.Add(path); return Id3(new MusicTag("TIT2", ["T"])); });

        var report = (await Service(new SameAudio(), tags).VerifyAsync(original, output, VerificationPolicy.Default, default)).Report;

        Assert.Equal(CheckOutcome.Passed, Outcome(report, VerificationEvaluator.KeptAudioCheckName));
        Assert.Equal(CheckOutcome.Passed, Outcome(report, VerificationEvaluator.TagsKeptCheckName));
        Assert.Equal([original.Path, output], read);
    }

    [Fact]
    public async Task Tags_lost_in_the_output_fail_the_job()
    {
        var (original, output) = Files();
        var tags = new Tags(path => path == output ? Id3(new MusicTag("TIT2", ["T"])) : Id3(new MusicTag("TIT2", ["T"]), new MusicTag("POPM", ["abc"])));

        var report = (await Service(new SameAudio(), tags).VerifyAsync(original, output, VerificationPolicy.Default, default)).Report;

        Assert.Equal(CheckOutcome.Failed, Outcome(report, VerificationEvaluator.TagsKeptCheckName));
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task Without_the_services_that_gather_the_evidence_both_checks_are_there_and_fail()
    {
        var (original, output) = Files();

        var report = (await Service(null, null).VerifyAsync(original, output, VerificationPolicy.Default, default)).Report;

        Assert.Equal(CheckOutcome.Failed, Outcome(report, VerificationEvaluator.KeptAudioCheckName));
        Assert.Equal(CheckOutcome.Failed, Outcome(report, VerificationEvaluator.TagsKeptCheckName));
    }

    [Fact]
    public async Task Evidence_from_a_worker_cannot_stand_in_for_the_comparisons()
    {
        var (original, output) = Files();
        const string probe = """{"streams":[{"codec_type":"audio","codec_name":"mp3","channels":2,"sample_rate":"44100","duration":"16"}],"format":{"duration":"16","format_name":"mp3"}}""";
        var evidence = new RemoteVerificationEvidence(Guid.NewGuid(), new('a', 64), new('b', 64), probe, probe, DecodeHealthResult.Ok,
            SourceAudio: new(true, 0, null, 16), CandidateAudio: new(true, 0, null, 10.5));
        var tags = new Tags(_ => Id3(new MusicTag("TIT2", ["T"])));

        var report = (await Service(new SameAudio(), tags).VerifyAsync(original, output, VerificationPolicy.Default, default, remoteEvidence: evidence)).Report;

        Assert.Equal(CheckOutcome.Failed, Outcome(report, VerificationEvaluator.KeptAudioCheckName));
        Assert.Equal(CheckOutcome.Failed, Outcome(report, VerificationEvaluator.TagsKeptCheckName));
    }

    [Fact]
    public async Task A_converted_music_file_has_neither_check()
    {
        var (original, output) = Files();

        var report = (await Service(new SameAudio(), new Tags(_ => Id3())).VerifyAsync(
            original with { AudioTrimOnly = null }, output, VerificationPolicy.Default, default)).Report;

        Assert.Null(Outcome(report, VerificationEvaluator.KeptAudioCheckName));
        Assert.Null(Outcome(report, VerificationEvaluator.TagsKeptCheckName));
    }

    private static readonly RuleSettings Rules = RuleProfileDefaults.For(RuleProfile.ConservativeHevc)
        with { TrimAudioSilence = true, TrimSilenceWithoutConverting = true, TargetAudioCodec = "opus" };

    private static TranscodeSpec TrimOnlySpec() =>
        TranscodeSpecResolver.Resolve(Rules, "/music/Track.mp3", "Track.mp3", "/work", sourceIsHdr: false, crf: null, preset: null,
            MediaKind.Audio, sourceMaxAudioChannels: 2, audioTrimOnly: Copy) with { SilenceTrim = Trim, TrimOnlyId3 = new(4, false) };

    [Fact]
    public void The_dispatchers_snapshot_of_a_trim_only_job_expects_its_own_codec_and_carries_the_trim()
    {
        var plain = new OriginalSnapshot("/music/Track.mp3", 6, 16, 1, 0, false, false, MediaKind.Audio);

        var snapshot = QueueDispatcher.WithSoundContract(plain, TrimOnlySpec(), Rules);

        // Not the library's target: the file keeps the codec it has.
        Assert.Equal("mp3", snapshot.ExpectedAudioCodec);
        Assert.Equal(Copy, snapshot.AudioTrimOnly);
        Assert.Equal(Trim, snapshot.SilenceTrim);
    }

    [Fact]
    public void The_dispatchers_snapshot_of_a_converted_music_file_expects_the_target_codec()
    {
        var plain = new OriginalSnapshot("/music/Track.flac", 6, 16, 1, 0, false, false, MediaKind.Audio);
        var converted = TranscodeSpecResolver.Resolve(Rules, "/music/Track.flac", "Track.flac", "/work", sourceIsHdr: false, crf: null, preset: null,
            MediaKind.Audio, sourceMaxAudioChannels: 2);

        var snapshot = QueueDispatcher.WithSoundContract(plain, converted, Rules);

        Assert.Equal("opus", snapshot.ExpectedAudioCodec);
        Assert.Null(snapshot.AudioTrimOnly);
    }

    [Fact]
    public void A_remote_worker_is_never_offered_a_trim_only_job()
    {
        // Even without a planned window: the comparisons with the source run only on this server.
        Assert.NotNull(QueueDispatcher.ServerOnlyRefusal(TrimOnlySpec()));
        Assert.NotNull(QueueDispatcher.ServerOnlyRefusal(TrimOnlySpec() with { SilenceTrim = null }));
        Assert.Null(QueueDispatcher.ServerOnlyRefusal(TrimOnlySpec() with { SilenceTrim = null, AudioTrimOnly = null }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
