using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;
using static Optimisarr.Tests.Mp4TagFixtures;

namespace Optimisarr.Tests;

/// <summary>The "Audio metadata and artwork" check on an M4A, which reads the file's own tag atoms (#593).</summary>
public sealed class Mp4TagVerificationTests
{
    private const string Check = "Audio metadata and artwork";

    private static VerificationInput Audio(Mp4TagEvidence? evidence, Dictionary<string, string>? source = null, Dictionary<string, string>? output = null) =>
        new(DecodeSucceeded: true, DecodeError: null, DecodeErrorCount: 0, OutputProbeSucceeded: true, OutputProbeError: null, OutputVideoCodec: null,
            OriginalDurationSeconds: 100, OutputDurationSeconds: 100, OriginalAudioTrackCount: 1, OutputAudioTrackCount: 1,
            OriginalSubtitleTrackCount: 0, OutputSubtitleTrackCount: 0, OriginalSizeBytes: 1000, OutputSizeBytes: 500,
            Kind: MediaKind.Audio, OriginalFormatTags: source ?? new() { ["title"] = "Title" },
            OutputFormatTags: output ?? new() { ["title"] = "Title" }, Mp4Tags: evidence);

    private static Mp4TagRead Read(byte[] file) => Mp4TagReader.Read(new MusicTagBytes(file));

    private static VerificationCheck Outcome(VerificationInput input) =>
        VerificationEvaluator.Evaluate(input, VerificationPolicy.Default).Checks.Single(check => check.Name == Check);

    private static readonly byte[] Converted = M4a(Text("©nam", "Title"), Text("©too", "Lavf61.7.103"),
        Freeform("com.optimisarr", "optimisarr", "1.2.3"));

    [Fact]
    public void An_m4a_with_itunes_tags_passes()
    {
        var check = Outcome(Audio(new Mp4TagEvidence(null, Read(Converted))));

        Assert.Equal(CheckOutcome.Passed, check.Outcome);
        Assert.Contains("iTunes", check.Detail);
    }

    [Fact]
    public void An_m4a_whose_tags_are_quicktime_keys_fails_although_ffprobe_reads_them_all()
    {
        var keys = Read([.. Ftyp, .. Mdat, .. Moov(QuickTimeKeysMeta(("optimisarr", "1.2.3"), ("TITLE", "Title")))]);

        var check = Outcome(Audio(new Mp4TagEvidence(null, keys)));

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("QuickTime", check.Detail);
    }

    [Fact]
    public void Tags_that_cannot_be_read_fail()
    {
        Assert.Equal(CheckOutcome.Failed, Outcome(Audio(new Mp4TagEvidence(null, Read(Converted[..^3])))).Outcome);
        Assert.Equal(CheckOutcome.Failed, Outcome(Audio(new Mp4TagEvidence(null, null))).Outcome);
        Assert.Equal(CheckOutcome.Failed, Outcome(Audio(new Mp4TagEvidence(Read(Converted[..^3]), Read(Converted)))).Outcome);
    }

    [Fact]
    public void An_m4a_source_must_keep_every_item_except_those_written_for_the_new_file()
    {
        var source = Read(M4a(Text("©nam", "Title"), Text("©too", "iTunes"), Freeform("com.apple.iTunes", "iTunSMPB", " 0"),
            Box("covr", DataBox(14, Png))));
        var kept = Read(M4a(Text("©nam", "Title"), Box("covr", DataBox(14, Png)), Text("©too", "Lavf"),
            Freeform("com.optimisarr", "optimisarr", "1.2.3")));
        var lost = Read(M4a(Text("©nam", "Title"), Text("©too", "Lavf")));
        var changed = Read(M4a(Text("©nam", "Title"), Box("covr", DataBox(13, Png)), Text("©too", "Lavf")));
        var gained = Read(M4a(Text("©nam", "Title"), Box("covr", DataBox(14, Png)), Text("©cmt", "New")));

        Assert.Equal(CheckOutcome.Passed, Outcome(Audio(new Mp4TagEvidence(source, kept))).Outcome);
        Assert.Contains("lost covr", Outcome(Audio(new Mp4TagEvidence(source, lost))).Detail);
        Assert.Contains("changed covr", Outcome(Audio(new Mp4TagEvidence(source, changed))).Detail);
        Assert.Contains("gained ©cmt", Outcome(Audio(new Mp4TagEvidence(source, gained))).Detail);
    }

    [Fact]
    public void A_flac_tag_carried_over_under_its_m4a_name_counts_as_kept()
    {
        var input = Audio(new Mp4TagEvidence(null, Read(Converted)),
            source: new() { ["TITLE"] = "Title", ["MUSICBRAINZ_ALBUMID"] = "abc", ["track"] = "3", ["TRACKTOTAL"] = "12" },
            output: new() { ["title"] = "Title", ["MusicBrainz Album Id"] = "abc", ["track"] = "3/12", ["optimisarr"] = "1.2.3" });

        Assert.Equal(CheckOutcome.Passed, Outcome(input).Outcome);
    }

    [Fact]
    public void A_lost_track_total_still_fails()
    {
        var input = Audio(new Mp4TagEvidence(null, Read(Converted)),
            source: new() { ["track"] = "3", ["TRACKTOTAL"] = "12" }, output: new() { ["track"] = "3" });

        Assert.Equal(CheckOutcome.Failed, Outcome(input).Outcome);
    }
}
