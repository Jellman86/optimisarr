using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafDecodeProgressTests
{
    private static QualityMeasurementContext Window => new(320, 180, false, false,
        ReferenceStartSeconds: 10, DistortedStartSeconds: 10, MeasureDurationSeconds: 2,
        ReferenceFrameRate: 50, PairFramesByNumber: true, ModelVersion: "vmaf_v0.6.1");

    [Theory]
    [InlineData(VmafAcceleration.None)]
    [InlineData(VmafAcceleration.Cuda)]
    [InlineData(VmafAcceleration.Qsv)]
    [InlineData(VmafAcceleration.Vaapi)]
    public void Sequential_windows_report_each_input_before_trimming_without_changing_measurement(VmafAcceleration acceleration)
    {
        var context = Window with { Acceleration = acceleration };
        var canonical = QualityScoreCommandBuilder.Build("candidate", "reference", "score", context, 2);
        var tracked = VmafDecodeProgress.Prepare(canonical, context);
        Assert.Equal(500, tracked.CandidateEndFrame);
        Assert.Equal(500, tracked.ReferenceEndFrame);
        Assert.Equal(canonical.ModelVersion, tracked.Command.ModelVersion);
        Assert.Equal(canonical.Preprocessing, tracked.Command.Preprocessing);
        Assert.DoesNotContain("-ss", tracked.Command.Arguments);
        Assert.Equal(2, tracked.Command.FilterGraph.Split("trim=start_frame=500:end_frame=600").Length - 1);
        Assert.Contains("file='pipe\\:1':direct=1", tracked.Command.FilterGraph);
        Assert.Contains("file='pipe\\:2':direct=1", tracked.Command.FilterGraph);
        Assert.Contains("lt(n,500)", tracked.Command.FilterGraph);
        Assert.DoesNotContain("metadata=", canonical.FilterGraph);
        Assert.Equal(canonical.Arguments.Count, tracked.Command.Arguments.Count);
    }

    [Fact]
    public void Full_file_timestamp_and_cut_clip_commands_keep_their_existing_graphs()
    {
        foreach (var context in new[] { Window with { PairFramesByNumber = false },
            Window with { DistortedIsCutClip = true },
            Window with { ReferenceStartSeconds = null, DistortedStartSeconds = null, MeasureDurationSeconds = null },
            Window with { ReferenceStartSeconds = 0, DistortedStartSeconds = 0 } })
        {
            var command = QualityScoreCommandBuilder.Build("candidate", "reference", "score", context, 2);
            Assert.Same(command, VmafDecodeProgress.Prepare(command, context).Command);
        }
    }

    [Fact]
    public void Only_new_paired_decoded_frame_counters_keep_the_watchdog_alive()
    {
        var progress = new VmafDecodedFrameProgress("candidate", 500);
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("frame:0 pts:0 pts_time:0"));
        Assert.Equal(DecodeProgressReading.Advanced, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        progress.Observe("frame:50 pts:1000 pts_time:1");
        Assert.Equal(DecodeProgressReading.Advanced, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        progress.Observe("frame:50 pts:1000 pts_time:1");
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        progress.Observe("frame:0 pts:0 pts_time:0");
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        Assert.Equal(50, progress.LastFrame);
    }

    [Theory]
    [InlineData("frame:500 pts:10000 pts_time:10")]
    [InlineData("frame:1000 pts:20000 pts_time:20")]
    [InlineData("frame:51 pts:1020 pts_time:1.02")]
    [InlineData("frame:-50 pts:0 pts_time:0")]
    [InlineData("frame:999999999999999999999999 pts:0 pts_time:0")]
    [InlineData("frame=50 fps=0 time=00:00:00.00 speed=0x")]
    public void Invalid_out_of_window_or_ordinary_stats_cannot_claim_decoding_progress(string header)
    {
        var progress = new VmafDecodedFrameProgress("candidate", 500);
        progress.Observe(header);
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        Assert.Equal(-1, progress.LastFrame);
    }

    [Fact]
    public void Input_identity_and_complete_records_are_required()
    {
        var progress = new VmafDecodedFrameProgress("reference", 500);
        progress.Observe("frame:50 pts:1000 pts_time:1");
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.candidate=1"));
        Assert.Equal(DecodeProgressReading.Telemetry, progress.Observe("lavfi.optimisarr.decode.reference=1"));
        progress.Observe("frame:50 pts:1000 pts_time:1");
        Assert.Equal(DecodeProgressReading.Advanced, progress.Observe("lavfi.optimisarr.decode.reference=1"));
        Assert.Equal(DecodeProgressReading.Unrelated, progress.Observe("Actual decoder error"));
    }

    [Fact]
    public void Asymmetric_fractional_cadence_windows_follow_the_exact_trim_boundaries()
    {
        var context = Window with { ReferenceFrameRate = 24000.0 / 1001, ReferenceStartSeconds = 11, DistortedStartSeconds = 0 };
        var command = QualityScoreCommandBuilder.Build("candidate", "reference", "score", context, 2);
        var tracked = VmafDecodeProgress.Prepare(command, context);
        Assert.Equal(264, tracked.ReferenceEndFrame);
        Assert.Equal(0, tracked.CandidateEndFrame);
        Assert.Contains("lt(n,264)", tracked.Command.FilterGraph);
        Assert.Contains("trim=start_frame=264:end_frame=312", tracked.Command.FilterGraph);
        Assert.DoesNotContain("lavfi.optimisarr.decode.candidate", tracked.Command.FilterGraph);
        Assert.Contains("[0:v]trim=start_frame=0:end_frame=48", tracked.Command.FilterGraph);
    }
}
