using Optimisarr.Core.Verification;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class QualityScoreCommandBuilderTests
{
    [Fact]
    public void Sdr_measurement_aligns_timebases_normalises_range_and_scales_bicubic()
    {
        var command = QualityScoreCommandBuilder.Build(
            distortedPath: "/work/output.mkv",
            referencePath: "/data/original.mkv",
            logPath: "/tmp/vmaf.json",
            new QualityMeasurementContext(1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 4);

        Assert.Equal("vmaf_v0.6.1", command.ModelVersion);
        Assert.Equal("SDR", command.Preprocessing);
        Assert.Equal("/work/output.mkv", ValueAfter(command.Arguments, "-i", occurrence: 1));
        Assert.Equal("/data/original.mkv", ValueAfter(command.Arguments, "-i", occurrence: 2));
        Assert.Contains("[0:v]settb=AVTB,setpts=PTS-STARTPTS,scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[dist]", command.FilterGraph);
        Assert.Contains("[1:v]settb=AVTB,setpts=PTS-STARTPTS,scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref]", command.FilterGraph);
        Assert.Contains("model=version=vmaf_v0.6.1", command.FilterGraph);
        Assert.Contains("n_threads=4", command.FilterGraph);
        Assert.Contains("n_subsample=1", command.FilterGraph);
        Assert.DoesNotContain("feature=", command.FilterGraph);
        Assert.Contains("shortest=1:repeatlast=0", command.FilterGraph);
        Assert.DoesNotContain("scale2ref", command.FilterGraph);
    }

    [Fact]
    public void Frame_subsampling_is_passed_to_libvmaf()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                FrameSubsample: 4),
            threads: 2);

        Assert.Contains("n_subsample=4", command.FilterGraph);
        Assert.Contains("every 4th frame", command.Preprocessing);
    }

    [Fact]
    public void Cuda_measurement_keeps_sdr_frames_on_the_gpu()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                Acceleration: VmafAcceleration.Cuda),
            threads: 2);

        Assert.Equal(2, command.Arguments.Count(argument => argument == "-hwaccel"));
        Assert.Equal(2, command.Arguments.Count(argument => argument == "-hwaccel_output_format"));
        Assert.Contains("scale_cuda=1920:1080:interp_algo=bicubic:format=yuv420p", command.FilterGraph);
        Assert.Contains("libvmaf_cuda=", command.FilterGraph);
        Assert.DoesNotContain("hwdownload", command.FilterGraph);
        Assert.Contains("CUDA VMAF", command.Preprocessing);
    }

    [Fact]
    public void Qsv_decode_downloads_frames_for_cpu_vmaf()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                Acceleration: VmafAcceleration.Qsv),
            threads: 2);

        Assert.Equal("qsv=hw", ValueAfter(command.Arguments, "-init_hw_device", occurrence: 1));
        Assert.Equal(2, command.Arguments.Count(argument => argument == "-hwaccel"));
        Assert.Contains("hwdownload,format=nv12", command.FilterGraph);
        Assert.Contains("libvmaf=", command.FilterGraph);
        Assert.DoesNotContain("libvmaf_cuda", command.FilterGraph);
    }

    [Fact]
    public void Vaapi_decode_downloads_frames_for_cpu_vmaf()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                Acceleration: VmafAcceleration.Vaapi),
            threads: 2);

        Assert.Equal("/dev/dri/renderD128", ValueAfter(command.Arguments, "-vaapi_device", occurrence: 1));
        Assert.Equal(2, command.Arguments.Count(argument => argument == "-hwaccel"));
        Assert.Contains("hwdownload,format=nv12", command.FilterGraph);
    }

    [Fact]
    public void Hdr_measurement_ignores_requested_acceleration_to_preserve_the_colour_pipeline()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: true, HdrConvertedToSdr: true,
                Acceleration: VmafAcceleration.Cuda),
            threads: 2);

        Assert.DoesNotContain("-hwaccel", command.Arguments);
        Assert.Contains("libvmaf=", command.FilterGraph);
        Assert.DoesNotContain("libvmaf_cuda", command.FilterGraph);
        Assert.Equal("HDR reference tone-mapped to SDR", command.Preprocessing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Invalid_frame_subsampling_is_rejected(int frameSubsample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                FrameSubsample: frameSubsample),
            threads: 1));
    }

    [Fact]
    public void Measurement_requests_progress_stats_for_the_queue()
    {
        var command = QualityScoreCommandBuilder.Build(
            "/work/output.mkv", "/data/original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 4);

        // -stats makes ffmpeg emit per-frame "time=" progress even at the error log level, which
        // the verification service turns into live queue progress.
        Assert.Contains("-stats", command.Arguments);
    }

    [Fact]
    public void Clip_measurement_seeks_before_the_window_and_trims_matching_decoder_preroll()
    {
        var command = QualityScoreCommandBuilder.Build(
            "/work/output.mkv", "/data/original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                DistortedStartSeconds: 300, ReferenceStartSeconds: 300, MeasureDurationSeconds: 120),
            threads: 4);

        var args = command.Arguments;
        // Decode five seconds of pre-roll on both independently encoded inputs, then trim the same
        // interval after decode. This avoids scoring different keyframe/decoder startup regions.
        Assert.Equal("295", ValueAfter(args, "-ss", occurrence: 1));
        Assert.Equal("/work/output.mkv", ValueAfter(args, "-i", occurrence: 1));
        Assert.Equal("295", ValueAfter(args, "-ss", occurrence: 2));
        Assert.Equal("/data/original.mkv", ValueAfter(args, "-i", occurrence: 2));
        Assert.Equal(2, command.FilterGraph.Split("trim=start=5:duration=120").Length - 1);
        Assert.Equal("120", ValueAfter(args, "-t", occurrence: 1));
    }

    [Fact]
    public void Clip_near_the_start_decodes_from_zero_and_trims_to_the_exact_window()
    {
        var command = QualityScoreCommandBuilder.Build(
            "/work/output.mkv", "/data/original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                DistortedStartSeconds: 3, ReferenceStartSeconds: 3, MeasureDurationSeconds: 40),
            threads: 4);

        Assert.DoesNotContain("-ss", command.Arguments);
        Assert.Equal(2, command.FilterGraph.Split("trim=start=3:duration=40").Length - 1);
    }

    [Fact]
    public void Full_file_measurement_has_no_seek_or_duration_cap()
    {
        var command = QualityScoreCommandBuilder.Build(
            "/work/output.mkv", "/data/original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 4);

        Assert.DoesNotContain("-ss", command.Arguments);
        Assert.DoesNotContain("-t", command.Arguments);
    }

    [Fact]
    public void Uhd_measurement_selects_the_4k_model_automatically()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(3840, 2160, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 2);

        Assert.Equal("vmaf_4k_v0.6.1", command.ModelVersion);
        Assert.Contains("model=version=vmaf_4k_v0.6.1", command.FilterGraph);
    }

    [Fact]
    public void Preview_measurement_seeks_only_the_decoded_reference_before_its_input()
    {
        var command = QualityScoreCommandBuilder.Build(
            "/work/preview.mkv", "/data/original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(
                1920, 1080, ReferenceIsHdr: false, HdrConvertedToSdr: false,
                ReferenceStartSeconds: 1770),
            threads: 2);

        var firstInput = IndexOf(command.Arguments, "-i", occurrence: 1);
        var referenceSeek = IndexOf(command.Arguments, "-ss", occurrence: 1);
        var secondInput = IndexOf(command.Arguments, "-i", occurrence: 2);

        Assert.True(firstInput < referenceSeek);
        Assert.Equal("1770", command.Arguments[referenceSeek + 1]);
        Assert.True(referenceSeek < secondInput);
        Assert.Equal("/data/original.mkv", command.Arguments[secondInput + 1]);
    }

    [Fact]
    public void Cropped_uhd_measurement_still_selects_the_4k_model()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(3840, 1608, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 2);

        Assert.Equal("vmaf_4k_v0.6.1", command.ModelVersion);
    }

    [Fact]
    public void Hdr_to_sdr_measurement_tone_maps_only_the_reference_with_the_production_chain()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(1920, 1080, ReferenceIsHdr: true, HdrConvertedToSdr: true),
            threads: 1);

        Assert.Equal("HDR reference tone-mapped to SDR", command.Preprocessing);
        Assert.DoesNotContain("tonemap", command.FilterGraph.Split("[dist]")[0]);
        Assert.Contains(HdrToneMap.Filter, command.FilterGraph);
        Assert.Contains($"[1:v]settb=AVTB,setpts=PTS-STARTPTS,{HdrToneMap.Filter},scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref]", command.FilterGraph);
    }

    [Fact]
    public void Hdr_preservation_keeps_both_inputs_in_their_native_transfer_domain()
    {
        var command = QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(1920, 1080, ReferenceIsHdr: true, HdrConvertedToSdr: false),
            threads: 1);

        Assert.Equal("HDR (matching transfer characteristics)", command.Preprocessing);
        Assert.DoesNotContain("tonemap", command.FilterGraph);
        Assert.Equal(2, command.FilterGraph.Split("format=yuv420p10le").Length - 1);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void Invalid_reference_dimensions_are_rejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityScoreCommandBuilder.Build(
            "output.mkv", "original.mkv", "/tmp/vmaf.json",
            new QualityMeasurementContext(width, height, ReferenceIsHdr: false, HdrConvertedToSdr: false),
            threads: 1));
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option, int occurrence)
    {
        var seen = 0;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == option && ++seen == occurrence)
            {
                return arguments[index + 1];
            }
        }

        throw new InvalidOperationException($"Missing occurrence {occurrence} of {option}.");
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string option, int occurrence)
    {
        var seen = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == option && ++seen == occurrence)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Missing occurrence {occurrence} of {option}.");
    }
}
