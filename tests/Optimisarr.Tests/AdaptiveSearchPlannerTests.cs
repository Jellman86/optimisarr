using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// The commands a worker is given to measure one candidate quality. These are what make running
/// the search where the encode runs possible at all, so what is pinned here is that nothing in
/// them names this machine, and that a sample is measured as a sample rather than as a slice of a
/// finished file.
/// </summary>
public sealed class AdaptiveSearchPlannerTests
{
    private static readonly VmafWindow[] Windows =
    [
        new VmafWindow(120, 40),
        new VmafWindow(600, 40),
        new VmafWindow(1080, 40),
    ];

    private static TranscodeSpec Spec() => new(
        InputPath: "/library/The Dinosaurs S01E01.mkv",
        OutputPath: "/work/7139/candidate.mkv",
        VideoCodec: "hevc",
        Crf: 24,
        Preset: "medium",
        TonemapToSdr: false);

    private static AdaptiveSearchStep? Plan(int quality = 24) =>
        AdaptiveSearchPlanner.Plan(
            quality,
            Spec(),
            videoEncoder: "hevc_videotoolbox",
            outputExtension: ".mkv",
            Windows,
            VerificationPolicy.Default with { QualityGateEnabled = true, ClipVmafEnabled = true },
            referenceWidth: 1920,
            referenceHeight: 1080,
            referenceIsHdr: false,
            hdrConvertedToSdr: false,
            sourceVideoDurationSeconds: 1389.638,
            referenceFrameRate: 24000d / 1001d,
            referenceContainerLeadSeconds: 0.021,
            crop: null,
            decimation: null);

    [Fact]
    public void Nothing_in_the_commands_names_a_path_on_this_machine()
    {
        // The worker substitutes its own copy of the source and its own scratch. A real path here
        // would be a path that does not exist on the machine asked to run it.
        var step = Plan();

        Assert.NotNull(step);
        foreach (var command in step!.SampleCommands)
        {
            Assert.Contains(WorkerProtocol.InputPlaceholder, command);
            Assert.Contains(command, argument => argument.StartsWith(WorkerProtocol.OutputPlaceholder, StringComparison.Ordinal));
            Assert.DoesNotContain(command, argument => argument.Contains("/library/", StringComparison.Ordinal));
            Assert.DoesNotContain(command, argument => argument.Contains("/work/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_candidate_is_expressed_on_the_scale_of_the_encoder_that_will_run_it()
    {
        // The point of moving the search: a quality proven on one encoder means nothing on
        // another. The candidate arrives as the library's CRF and leaves as whatever that encoder
        // actually takes — here VideoToolbox's own quality scale, not the number the search names.
        var command = Plan(quality: 30)!.SampleCommands[0].ToList();

        Assert.Contains("hevc_videotoolbox", command);
        Assert.Contains("-q:v", command);
        Assert.DoesNotContain("-crf", command);
    }

    [Fact]
    public void Each_window_is_encoded_as_a_clip_of_video_alone()
    {
        var step = Plan();

        Assert.Equal(Windows.Length, step!.SampleCommands.Count);
        for (var index = 0; index < Windows.Length; index++)
        {
            var command = step.SampleCommands[index].ToList();
            // A coarse seek before the input and a fine one after it, which together land on the
            // window: accurate seeking on a long-GOP source needs both.
            var coarse = double.Parse(command[command.IndexOf("-ss") + 1]);
            var fine = double.Parse(command[command.LastIndexOf("-ss") + 1]);
            // These windows are always bounded — VmafWindow.Full, the whole-file case, is never
            // used for a search — so the values are read rather than defaulted.
            Assert.Equal((double)Windows[index].StartSeconds!.Value, coarse + fine, 3);
            Assert.Equal(
                Windows[index].DurationSeconds!.Value.ToString(),
                command[command.IndexOf("-t") + 1]);
            // Video only, by mapping the picture rather than excluding everything else: copied
            // audio would swamp the byte comparison the search selects on.
            Assert.Contains("0:v:0", command);
            Assert.DoesNotContain(command, argument => argument.StartsWith("0:a", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_sample_is_measured_from_its_own_first_frame_while_the_reference_seeks()
    {
        // The difference between a sample and a finished candidate. A sample file *is* the window,
        // so its timeline is normalised from its own first picture; seeking into it would score the
        // wrong forty seconds against the right ones. A delivered candidate is the whole file, and
        // only there does the worker have to report how far its first picture lags.
        var step = Plan();
        var measurement = step!.Measurement.Commands[1].ToList();
        var filter = measurement[measurement.IndexOf("-lavfi") + 1];

        Assert.Contains("[0:v]settb=AVTB,setpts=PTS-STARTPTS", filter);
        Assert.DoesNotContain(RemoteQualityContract.DistortedShiftPlaceholder, filter);
        // The reference is trimmed to where this window actually sits in the source.
        Assert.Contains($"trim=start=", filter);
    }

    [Fact]
    public void The_thresholds_travel_with_the_commands_that_will_be_judged_against_them()
    {
        // Evidence taken under one policy is evidence about something else under another, so the
        // policy the worker measured against is recorded alongside what it measured.
        var policy = VerificationPolicy.Default with
        {
            QualityGateEnabled = true,
            ClipVmafEnabled = true,
            MinimumVmafHarmonicMean = 93,
            MinimumVmafMin = 80,
        };

        var step = AdaptiveSearchPlanner.Plan(
            24, Spec(), "hevc_videotoolbox", ".mkv", Windows, policy,
            1920, 1080, false, false, 1389.638, 24000d / 1001d, 0.021, null, null);

        Assert.Equal(93, step!.Measurement.MinimumHarmonicMean);
        Assert.Equal(80, step.Measurement.MinimumMinimum);
        Assert.Equal(Windows.Length, step.Measurement.WindowCount);
    }

    [Fact]
    public void A_library_with_the_quality_gate_off_plans_nothing()
    {
        // There would be nothing to judge the evidence against, so the sample encodes would be run
        // for nothing. The local search declines the same case with "the VMAF target is disabled".
        Assert.Null(AdaptiveSearchPlanner.Plan(
            24, Spec(), "hevc_videotoolbox", ".mkv", Windows,
            VerificationPolicy.Default with { QualityGateEnabled = false },
            1920, 1080, false, false, 1389.638, 24000d / 1001d, 0.021, null, null));
    }

    [Fact]
    public void A_source_whose_dimensions_are_unknown_plans_nothing()
    {
        // Rather than planning a measurement that cannot pick a VMAF model. The caller falls back
        // to searching locally, which is the behaviour that existed before any of this.
        Assert.Null(AdaptiveSearchPlanner.Plan(
            24, Spec(), "hevc_videotoolbox", ".mkv", Windows,
            VerificationPolicy.Default with { QualityGateEnabled = true },
            referenceWidth: 0, referenceHeight: 0,
            referenceIsHdr: false, hdrConvertedToSdr: false,
            sourceVideoDurationSeconds: 1389.638, referenceFrameRate: 24, referenceContainerLeadSeconds: 0,
            crop: null, decimation: null));
    }
}
