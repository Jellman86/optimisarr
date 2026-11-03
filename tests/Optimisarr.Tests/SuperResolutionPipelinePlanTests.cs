using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// The plan decides everything before a process starts: where the frame trees go, the exact
/// arguments of the three stages, and the frame count everything downstream is held to. The
/// refusals keep an unsuitable source — HDR, VFR, a hardware encoder — away from a pipeline
/// that would produce a wrong result, and fail with the reason instead of silently running.
/// </summary>
public sealed class SuperResolutionPipelinePlanTests
{
    private static readonly SuperResolutionOptions Options = SuperResolutionOptions.Defaults;

    private static QueueDispatcher.JobWork VideoWork(
        bool variableFrameRate = false,
        bool isHdr = false,
        CropRect? CropTo = null,
        PictureSize? DownscaleTo = null,
        FrameRateDecimation? FrameRate = null) =>
        new(
            Spec: Spec(variableFrameRate) with { CropTo = CropTo, DownscaleTo = DownscaleTo, FrameRate = FrameRate },
            Arguments: ["-version"],
            VideoEncoder: "libx265",
            VideoQuality: new EncoderQuality(20, 20, "Crf", 0),
            IsDisposable: false,
            IsCalibration: false,
            DurationSeconds: 40,
            FrameCount: 960,
            VideoFrameRate: 24,
            MoveOnComplete: false,
            TargetFolder: null,
            MoveOverwrite: false,
            MediaFileId: 42,
            Original: new OriginalSnapshot("/media/source.mkv", 10_000_000, 40, 1, 0, isHdr, isHdr),
            VerificationPolicy: VerificationPolicy.Default,
            VideoQualityStrategy: VideoQualityStrategy.Fixed,
            Placement: WorkPlacement.Anywhere,
            AdaptiveVideoQuality: null,
            AutoReplace: false,
            CpuThreadLimit: 4,
            HardwareDecodeRequested: false,
            HardwareToneMapRequested: false,
            SourcePicture: new PictureSize(640, 360));

    private static TranscodeSpec Spec(bool variableFrameRate = false) => new TranscodeSpec(
            "/media/source.mkv", "/work/42/candidate.mkv",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false,
            SourceIsVariableFrameRate: variableFrameRate);

    [Fact]
    public void Each_attempt_gets_its_own_scratch_outside_every_media_files_work_folder()
    {
        var first = WorkOutputRoot.ForSuperResolution("/work", 42, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = WorkOutputRoot.ForSuperResolution("/work", 42, Guid.Parse("00000000-0000-0000-0000-000000000002"));

        Assert.NotEqual(first, second);
        Assert.StartsWith("/work/super-resolution/", first);
        Assert.DoesNotContain("/work/42/", first);
    }

    [Fact]
    public void Startup_clears_only_the_super_resolution_scratch_root()
    {
        var work = Directory.CreateTempSubdirectory("optimisarr-sr-scratch-").FullName;
        try
        {
            var stale = WorkOutputRoot.ForSuperResolution(work, 7, Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(stale, "in-000000"));
            File.WriteAllText(Path.Combine(stale, "in-000000", "f00000000.png"), "stale");
            var candidate = Path.Combine(work, "7", "candidate.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            File.WriteAllText(candidate, "kept");

            SuperResolutionScratch.ClearAtStartup(work);

            Assert.False(Directory.Exists(stale));
            Assert.True(File.Exists(candidate));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void An_hdr_source_is_refused_before_anything_runs()
    {
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(VideoWork(isHdr: true), Options));
    }

    [Fact]
    public void A_variable_frame_rate_source_is_refused()
    {
        var work = VideoWork(variableFrameRate: true);
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(work, Options));
    }

    [Fact]
    public void A_hardware_encoder_is_refused()
    {
        var work = VideoWork() with { VideoEncoder = "hevc_nvenc" };
        var problem = Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(work, Options));
        Assert.Contains("hevc_nvenc", problem.Message);
    }

    [Fact]
    public void An_invalid_engine_configuration_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(VideoWork(), Options with { Scale = 9 }));
    }

    [Fact]
    public void Library_geometry_rules_are_refused_instead_of_being_silently_ignored()
    {
        // The spec these rules produce is what a super-resolution job would have to contradict:
        // the engine upscales every frame, so a crop, a downscale or a frame-rate cap either
        // applies before it (changing what it sees) or is dropped (changing what was promised).
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(
                VideoWork(CropTo: new CropRect(640, 360, 0, 0)), Options));
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(
                VideoWork(DownscaleTo: new PictureSize(360, 180)), Options));
        Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(
                VideoWork(FrameRate: new FrameRateDecimation(60, 30, 2)), Options));
    }

    [Fact]
    public void An_sdr_cfr_source_with_a_software_encoder_passes_the_refusal_gate()
    {
        QueueDispatcher.RefuseUnsuitableSource(VideoWork(), Options);
        Assert.True(true); // reaching here is the assertion: nothing threw
    }

    [Fact]
    public void A_source_without_probed_dimensions_is_refused()
    {
        // The candidate's expected size is the promise verification holds the output to. A
        // source whose dimensions the inventory never captured gives no honest expectation,
        // and guessing one would turn the dimension gate into either a rubber stamp or a
        // false accusation.
        var work = VideoWork() with { SourcePicture = null };
        var problem = Assert.Throws<InvalidOperationException>(() =>
            QueueDispatcher.RefuseUnsuitableSource(work, Options));
        Assert.Contains("dimensions", problem.Message, StringComparison.OrdinalIgnoreCase);
    }
}
