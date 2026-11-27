using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class AudioTrimOnlyTests
{
    [Theory]
    [InlineData("flac", "Album/Track.flac", "flac")]
    [InlineData("pcm_s16le", "Album/Track.wav", "pcm_s16le")]
    [InlineData("pcm_s24le", "Album/Track.WAV", "pcm_s24le")]
    public void Lossless_files_are_re_encoded_losslessly_in_their_own_codec(string codec, string path, string encoder)
    {
        var decision = AudioTrimOnly.Decide(codec, path, attachedPictureCount: 0);

        Assert.Equal(new AudioTrimOnlyPlan(AudioTrimOnlyMethod.LosslessReencode, codec, encoder), decision.Plan);
    }

    [Theory]
    [InlineData("mp3", "Album/Track.mp3")]
    [InlineData("opus", "Album/Track.opus")]
    [InlineData("opus", "Album/Track.ogg")]
    public void Lossy_files_are_cut_by_copying_their_audio_frames(string codec, string path)
    {
        var decision = AudioTrimOnly.Decide(codec, path, attachedPictureCount: 0);

        Assert.Equal(new AudioTrimOnlyPlan(AudioTrimOnlyMethod.StreamCopy, codec, null), decision.Plan);
    }

    [Theory]
    [InlineData("flac", "Album/Track.flac")]
    [InlineData("mp3", "Album/Track.mp3")]
    public void Formats_that_carry_cover_art_keep_it(string codec, string path)
    {
        Assert.NotNull(AudioTrimOnly.Decide(codec, path, attachedPictureCount: 1).Plan);
    }

    [Theory]
    [InlineData("opus", "Album/Track.opus")]
    [InlineData("pcm_s16le", "Album/Track.wav")]
    public void Files_whose_cover_art_could_not_be_written_back_are_left_alone(string codec, string path)
    {
        var decision = AudioTrimOnly.Decide(codec, path, attachedPictureCount: 1);

        Assert.Null(decision.Plan);
        Assert.Contains("cover art", decision.Reason);
    }

    [Theory]
    // ADTS has no place for encoder delay and only estimates its length.
    [InlineData("aac", "Album/Track.aac")]
    [InlineData("vorbis", "Album/Track.ogg")]
    [InlineData("wmav2", "Album/Track.wma")]
    [InlineData("ape", "Album/Track.ape")]
    [InlineData("pcm_s16be", "Album/Track.aiff")]
    // A codec in a container it is not usually found in is not guessed at.
    [InlineData("mp3", "Album/Track.mp4")]
    [InlineData("flac", "Album/Track.ogg")]
    public void Other_formats_are_left_alone_with_the_reason(string codec, string path)
    {
        var decision = AudioTrimOnly.Decide(codec, path, attachedPictureCount: 0);

        Assert.Null(decision.Plan);
        Assert.Contains(codec, decision.Reason);
    }

    [Theory]
    [InlineData("aac", 0)]
    [InlineData("alac", 0)]
    [InlineData("aac", 1)]
    public void M4a_files_are_left_alone_because_their_tags_would_be_rewritten_as_quicktime_keys(string codec, int pictures)
    {
        var decision = AudioTrimOnly.Decide(codec, "Album/Track.m4a", pictures);

        Assert.Null(decision.Plan);
        Assert.Equal("M4A files are not yet trimmed without converting: it has not been proved that their audio and tags come through a trim unchanged.", decision.Reason);
    }

    [Fact]
    public void Tags_a_trim_would_not_keep_leave_the_file_alone_with_that_reason()
    {
        const string refusal = "Trimming it without converting would lose or change some of its tags (POPM), so it is left alone.";

        var decision = AudioTrimOnly.Decide("mp3", "Album/Track.mp3", 0, refusal);

        Assert.Null(decision.Plan);
        Assert.Equal(refusal, decision.Reason);
    }

    [Fact]
    public void A_stream_copy_starts_a_little_before_the_planned_start_and_keeps_the_planned_end()
    {
        var plan = new AudioTrimPlan(StartSample: 121_275, EndSample: 584_325, SampleRate: 44_100, SourceSeconds: 16, ThresholdDb: -60);

        var copied = AudioTrimOnly.ForStreamCopy(plan);

        // A copy can only start on a frame, and its seek can land up to a frame late.
        Assert.Equal(121_275 - 2_205, copied.StartSample);
        Assert.Equal(584_325, copied.EndSample);
        Assert.Equal(plan.KeptSeconds + 0.05, copied.KeptSeconds, precision: 9);
    }

    [Fact]
    public void A_start_closer_than_the_lead_is_not_cut_at_all()
    {
        var plan = new AudioTrimPlan(StartSample: 1_000, EndSample: 584_325, SampleRate: 44_100, SourceSeconds: 16, ThresholdDb: -60);

        var copied = AudioTrimOnly.ForStreamCopy(plan);

        Assert.False(copied.TrimsStart);
        Assert.True(copied.TrimsEnd);
    }

    [Fact]
    public void An_end_only_trim_is_unchanged_by_a_stream_copy()
    {
        var plan = new AudioTrimPlan(StartSample: 0, EndSample: 584_325, SampleRate: 44_100, SourceSeconds: 16, ThresholdDb: -60);

        Assert.Equal(plan, AudioTrimOnly.ForStreamCopy(plan));
    }

    private static readonly RuleSettings Rules = RuleProfileDefaults.For(RuleProfile.ConservativeHevc)
        with { TrimAudioSilence = true, TrimSilenceWithoutConverting = true };

    // 2.75 s off the start and 2.75 s off the end of a 16 s track at 44.1 kHz.
    private static readonly AudioTrimPlan Trim = new(121_275, 584_325, 44_100, 16, -60);

    private static TranscodeSpec Spec(string relativePath, AudioTrimOnlyPlan plan, AudioTrimPlan? trim, Id3Layout? id3 = null) =>
        TranscodeSpecResolver.Resolve(Rules, "/music/" + relativePath, relativePath, "/work", sourceIsHdr: false,
            crf: null, preset: null, MediaKind.Audio, sourceMaxAudioChannels: 2, audioTrimOnly: plan)
            with { SilenceTrim = trim, TrimOnlyId3 = id3 ?? (relativePath.EndsWith(".mp3") ? new(4, false) : null) };

    private static readonly AudioTrimOnlyPlan Flac = new(AudioTrimOnlyMethod.LosslessReencode, "flac", "flac");
    private static readonly AudioTrimOnlyPlan Copy = new(AudioTrimOnlyMethod.StreamCopy, "mp3", null);

    [Fact]
    public void A_trim_only_job_keeps_the_file_name_codec_and_channels()
    {
        var spec = Spec("Album/Track.FLAC", Flac, Trim);

        Assert.Equal("/work/Album/Track.FLAC", spec.OutputPath);
        Assert.Equal("flac", spec.AudioEncoder);
        Assert.Null(spec.AudioBitrateKbps);
        Assert.False(spec.DownmixToStereo);
        Assert.Equal(Flac, spec.AudioTrimOnly);
    }

    [Fact]
    public void A_lossless_trim_cuts_the_exact_samples_and_copies_tags_and_cover_art()
    {
        var args = FfmpegCommandBuilder.Build(Spec("Album/Track.flac", Flac, Trim));

        Assert.Equal(["-y", "-progress", "pipe:1", "-nostats", "-i", "/music/Album/Track.flac",
            "-map_metadata", "0", "-map", "0:v?", "-c:v", "copy", "-map", "0:a", "-c:a", "flac", "-compression_level", "12",
            "-af", "atrim=start_sample=121275:end_sample=584325,asetpts=PTS-STARTPTS", "/work/Album/Track.flac"], args);
    }

    [Fact]
    public void A_wav_has_no_picture_to_map()
    {
        var wav = FfmpegCommandBuilder.Build(Spec("Album/Track.wav", new(AudioTrimOnlyMethod.LosslessReencode, "pcm_s24le", "pcm_s24le"), Trim));

        Assert.Equal(["-map_metadata", "0", "-map", "0:a", "-c:a", "pcm_s24le"], wav.Skip(6).Take(6));
    }

    [Fact]
    public void An_mp3_copy_seeks_its_audio_and_takes_the_cover_art_from_a_second_unseeked_read()
    {
        // Cut by the output instead, FFmpeg drops an attached picture, which sits at the very start.
        var args = FfmpegCommandBuilder.Build(Spec("Album/Track.mp3", Copy, AudioTrimOnly.ForStreamCopy(Trim)));

        Assert.Equal(["-y", "-progress", "pipe:1", "-nostats", "-ss", "2.700000", "-i", "/music/Album/Track.mp3",
            "-i", "/music/Album/Track.mp3", "-map_metadata", "0", "-map", "1:v?", "-c:v", "copy", "-map", "0:a", "-c:a", "copy",
            // The source's iTunes gapless figures and length describe the whole file, not the cut.
            "-metadata", "iTunSMPB=", "-metadata", "TLEN=", "-t", "10.550000", "/work/Album/Track.mp3"], args);
        Assert.DoesNotContain("-af", args);
        // The job records the filter it ran, and a copy runs none.
        Assert.Null(FfmpegCommandBuilder.AudioFilterChain(Spec("Album/Track.mp3", Copy, Trim)));
        Assert.StartsWith("atrim=", FfmpegCommandBuilder.AudioFilterChain(Spec("Album/Track.flac", Flac, Trim)));
    }

    [Fact]
    public void An_mp3_copy_keeps_the_sources_id3_version_and_its_id3v1_tag()
    {
        var args = FfmpegCommandBuilder.Build(Spec("Album/Track.mp3", Copy, Trim, new Id3Layout(3, HasId3v1: true)));

        // 2.3 frames such as TYER, TDAT and TORY stay as they are only in a 2.3 tag.
        Assert.Equal(["-id3v2_version", "3"], args.SkipWhile(arg => arg != "-id3v2_version").Take(2));
        Assert.Equal(["-write_id3v1", "1"], args.SkipWhile(arg => arg != "-write_id3v1").Take(2));
        Assert.DoesNotContain("-id3v2_version", FfmpegCommandBuilder.Build(Spec("Album/Track.mp3", Copy, Trim, new Id3Layout(4, false))));
    }

    [Fact]
    public void An_mp3_copy_is_never_written_without_reading_the_sources_tags_first()
    {
        var spec = Spec("Album/Track.mp3", Copy, Trim) with { TrimOnlyId3 = null };

        Assert.Throws<InvalidOperationException>(() => FfmpegCommandBuilder.Build(spec));
    }

    [Fact]
    public void A_copy_that_keeps_its_start_reads_the_file_once_and_only_limits_the_length()
    {
        var endOnly = Trim with { StartSample = 0 };

        var args = FfmpegCommandBuilder.Build(Spec("Album/Track.mp3", Copy, endOnly));

        Assert.Equal(["-y", "-progress", "pipe:1", "-nostats", "-i", "/music/Album/Track.mp3",
            "-map_metadata", "0", "-map", "0:v?", "-c:v", "copy", "-map", "0:a", "-c:a", "copy",
            "-metadata", "iTunSMPB=", "-metadata", "TLEN=", "-t", "13.250000", "/work/Album/Track.mp3"], args);
    }

    [Fact]
    public void An_opus_copy_is_cut_on_the_output_side_because_its_seek_lands_far_too_early()
    {
        var opus = new AudioTrimPlan(132_000, 636_000, 48_000, 16, -60);

        var args = FfmpegCommandBuilder.Build(Spec("Album/Track.opus", Copy with { Codec = "opus" }, AudioTrimOnly.ForStreamCopy(opus)));

        Assert.Equal(["-y", "-progress", "pipe:1", "-nostats", "-i", "/music/Album/Track.opus",
            "-map_metadata", "0", "-map", "0:a", "-c:a", "copy", "-ss", "2.700000", "-t", "10.550000", "/work/Album/Track.opus"], args);
    }

    [Fact]
    public void A_trim_only_job_without_a_trim_is_refused_rather_than_copied_unchanged()
    {
        Assert.Throws<InvalidOperationException>(() => FfmpegCommandBuilder.Build(Spec("Album/Track.mp3", Copy, trim: null)));
        Assert.Throws<InvalidOperationException>(() => FfmpegCommandBuilder.Build(Spec("Album/Track.flac", Flac, trim: null)));
    }
}
