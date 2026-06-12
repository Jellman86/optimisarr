using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegCommandBuilderTests
{
    private static TranscodeSpec AudioReencode() =>
        new(
            InputPath: "/data/music/Track.flac",
            OutputPath: "/work/Track.opus",
            VideoCodec: null,
            Crf: null,
            Preset: null,
            TonemapToSdr: false,
            Kind: MediaKind.Audio,
            AudioEncoder: "libopus",
            AudioBitrateKbps: 128);

    [Fact]
    public void An_audio_job_re_encodes_audio_to_the_target_codec_and_bitrate()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        var audioCodecIndex = IndexOf(args, "-c:a");
        Assert.Equal("libopus", args[audioCodecIndex + 1]);
        var bitrateIndex = IndexOf(args, "-b:a");
        Assert.Equal("128k", args[bitrateIndex + 1]);
        Assert.Equal("/work/Track.opus", args[^1]);
    }

    [Fact]
    public void An_audio_job_does_not_re_encode_video_and_preserves_cover_art_and_metadata()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        // Cover art is copied, not re-encoded; metadata is carried over.
        var videoCodecIndex = IndexOf(args, "-c:v");
        Assert.Equal("copy", args[videoCodecIndex + 1]);
        Assert.DoesNotContain("libx265", args);
        Assert.DoesNotContain("-crf", args);
        var metaMapIndex = IndexOf(args, "-map_metadata");
        Assert.Equal("0", args[metaMapIndex + 1]);
    }

    [Fact]
    public void An_audio_job_still_stamps_the_optimisation_marker()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode(), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
    }

    private static TranscodeSpec Reencode(
        string? videoCodec = "hevc",
        int? crf = 23,
        string? preset = "medium",
        bool tonemap = false) =>
        new(
            InputPath: "/data/films/Movie.mkv",
            OutputPath: "/work/Movie.opt.mkv",
            VideoCodec: videoCodec,
            Crf: crf,
            Preset: preset,
            TonemapToSdr: tonemap);

    private static int IndexOf(IReadOnlyList<string> args, string value) =>
        ((List<string>)args).IndexOf(value);

    [Fact]
    public void Passes_input_and_output_paths_as_separate_arguments()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        // The path is its own argument (never interpolated into a shell string).
        var inputIndex = IndexOf(args, "-i");
        Assert.Equal("/data/films/Movie.mkv", args[inputIndex + 1]);
        Assert.Equal("/work/Movie.opt.mkv", args[^1]);
    }

    [Fact]
    public void Always_overwrites_its_own_work_output_and_maps_all_streams()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.Contains("-y", args);
        var mapIndex = IndexOf(args, "-map");
        Assert.Equal("0", args[mapIndex + 1]);
    }

    [Fact]
    public void Adds_global_thread_limit_when_configured()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), threads: 2);

        var threadsIndex = IndexOf(args, "-threads");
        Assert.Equal("2", args[threadsIndex + 1]);
        Assert.True(threadsIndex < IndexOf(args, "-i"));
    }

    [Fact]
    public void Omits_thread_limit_when_zero_or_negative()
    {
        Assert.DoesNotContain("-threads", FfmpegCommandBuilder.Build(Reencode(), threads: 0));
        Assert.DoesNotContain("-threads", FfmpegCommandBuilder.Build(Reencode(), threads: -1));
    }

    [Theory]
    [InlineData("hevc", "libx265")]
    [InlineData("h264", "libx264")]
    [InlineData("av1", "libsvtav1")]
    public void Maps_target_codec_to_the_expected_encoder(string codec, string encoder)
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: codec));

        var vIndex = IndexOf(args, "-c:v");
        Assert.Equal(encoder, args[vIndex + 1]);
    }

    [Fact]
    public void Uses_selected_video_encoder_when_provided()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: "hevc"), videoEncoder: "hevc_nvenc");

        var vIndex = IndexOf(args, "-c:v");
        Assert.Equal("hevc_nvenc", args[vIndex + 1]);
    }

    [Fact]
    public void Applies_crf_and_preset_when_re_encoding()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(crf: 28, preset: "slow"));

        var crfIndex = IndexOf(args, "-crf");
        Assert.Equal("28", args[crfIndex + 1]);
        var presetIndex = IndexOf(args, "-preset");
        Assert.Equal("slow", args[presetIndex + 1]);
    }

    [Fact]
    public void Copies_audio_and_subtitles_when_re_encoding_video()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("copy", args[audioIndex + 1]);
        var subIndex = IndexOf(args, "-c:s");
        Assert.Equal("copy", args[subIndex + 1]);
    }

    [Fact]
    public void Remux_only_copies_all_streams_and_never_re_encodes()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: null));

        Assert.DoesNotContain("-c:v", args);
        Assert.DoesNotContain("-crf", args);
        var cIndex = IndexOf(args, "-c");
        Assert.Equal("copy", args[cIndex + 1]);
    }

    [Fact]
    public void Adds_a_tonemap_filter_when_converting_hdr_to_sdr()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: true));

        var vfIndex = IndexOf(args, "-vf");
        Assert.True(vfIndex >= 0);
        Assert.Contains("tonemap", args[vfIndex + 1]);
    }

    [Fact]
    public void Does_not_add_a_tonemap_filter_otherwise()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: false));

        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Stamps_the_optimisation_marker_into_the_output_metadata()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.True(metaIndex >= 0);
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
        // The marker is an output option, before the output path.
        Assert.True(metaIndex < args.Count - 1);
    }

    [Fact]
    public void Stamps_the_marker_on_a_remux_too()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: null), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
    }

    [Fact]
    public void Adds_the_mp4_flag_so_custom_tags_round_trip_for_mp4_outputs()
    {
        var spec = Reencode() with { OutputPath = "/work/Movie.opt.mp4" };

        var args = FfmpegCommandBuilder.Build(spec, optimisedMarker: "0.4.2");

        var flagsIndex = IndexOf(args, "-movflags");
        Assert.True(flagsIndex >= 0);
        Assert.Equal("use_metadata_tags", args[flagsIndex + 1]);
    }

    [Fact]
    public void Does_not_add_the_mp4_flag_for_a_matroska_output()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), optimisedMarker: "0.4.2");

        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void Omits_the_marker_when_none_is_given()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.DoesNotContain("-metadata", args);
    }

    [Fact]
    public void Re_encodes_a_videos_audio_when_an_audio_encoder_is_set()
    {
        var spec = Reencode() with { AudioEncoder = "aac", AudioBitrateKbps = 160 };

        var args = FfmpegCommandBuilder.Build(spec);

        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("aac", args[audioIndex + 1]);
        var bitrateIndex = IndexOf(args, "-b:a");
        Assert.Equal("160k", args[bitrateIndex + 1]);
        // Video is still re-encoded and subtitles copied.
        Assert.Equal("libx265", args[IndexOf(args, "-c:v") + 1]);
        Assert.Equal("copy", args[IndexOf(args, "-c:s") + 1]);
    }

    [Fact]
    public void Re_encodes_the_audio_of_a_remux_only_job_when_requested()
    {
        var spec = Reencode(videoCodec: null) with { AudioEncoder = "aac", AudioBitrateKbps = 160 };

        var args = FfmpegCommandBuilder.Build(spec);

        // Video and subtitles still copy via the blanket "-c copy"; only audio is overridden.
        Assert.Equal("copy", args[IndexOf(args, "-c") + 1]);
        Assert.DoesNotContain("-c:v", args);
        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("aac", args[audioIndex + 1]);
        Assert.Equal("160k", args[IndexOf(args, "-b:a") + 1]);
    }

    [Fact]
    public void Copies_audio_with_no_bitrate_when_no_audio_encoder_is_set()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.Equal("copy", args[IndexOf(args, "-c:a") + 1]);
        Assert.DoesNotContain("-b:a", args);
    }

    [Fact]
    public void Downmixes_an_audio_job_to_stereo_when_requested()
    {
        var spec = AudioReencode() with { DownmixToStereo = true };

        var args = FfmpegCommandBuilder.Build(spec);

        var acIndex = IndexOf(args, "-ac");
        Assert.Equal("2", args[acIndex + 1]);
    }

    [Fact]
    public void Downmixes_a_videos_re_encoded_audio_to_stereo_when_requested()
    {
        var spec = Reencode() with { AudioEncoder = "aac", AudioBitrateKbps = 160, DownmixToStereo = true };

        var args = FfmpegCommandBuilder.Build(spec);

        Assert.Equal("aac", args[IndexOf(args, "-c:a") + 1]);
        Assert.Equal("2", args[IndexOf(args, "-ac") + 1]);
    }

    [Fact]
    public void Does_not_downmix_when_not_requested()
    {
        Assert.DoesNotContain("-ac", FfmpegCommandBuilder.Build(AudioReencode()));
        Assert.DoesNotContain("-ac", FfmpegCommandBuilder.Build(Reencode()));
    }
}
