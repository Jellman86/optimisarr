using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class WorkerScratchBudgetTests
{
    private const long MiB = 1024 * 1024;
    private static RemotePerceptualSearchContract Image() => new(Guid.NewGuid(), MediaKind.Image, "libwebp",
        PerceptualQualityPlan.Image("libwebp", 80), VerificationPolicy.Default with
            { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85 }, [], SourceImage: new(32, 32, false));

    [Fact]
    public void Tiny_image_sources_still_need_room_for_a_bounded_probe_and_metadata_rewrite()
    {
        Assert.Equal(1000 + 2 * 900 + 128 * MiB + 32 * MiB, WorkerScratchBudget.RequiredBytes(1000, 900, Image()));
        Assert.Equal(1000 + 900 + 128 * MiB + 32 * MiB,
            WorkerScratchBudget.RequiredBytes(1000, 900, Image() with { Policy = Image().Policy with { ImageMetadataGateEnabled = false } }));
    }

    [Fact]
    public void Missing_size_saving_ceiling_still_reserves_for_the_supported_native_image_file_budget()
    {
        Assert.Equal(1000 + 3 * 128 * MiB + 32 * MiB, WorkerScratchBudget.RequiredBytes(1000, null, Image()));
    }

    [Fact]
    public void Audio_workspace_covers_all_windows_references_and_native_pcm_pairs()
    {
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Audio, "aac", PerceptualQualityPlan.Audio("aac", 128),
            VerificationPolicy.Default with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02 },
            PerceptualSamplePlanner.AudioWindows(180), SourceAudio: new(180, 2, 48000, "stereo"));
        Assert.Equal(1000 + 900 + (3 * 128 + 3 * 16 + 24 + 32) * MiB, WorkerScratchBudget.RequiredBytes(1000, 900, contract));
        Assert.True(WorkerScratchBudget.RequiredBytes(1000, null, contract) > WorkerScratchBudget.RequiredBytes(1000, 900, contract));
    }

    [Fact]
    public void Invalid_or_overflowing_inputs_cannot_wrap_into_an_affordable_requirement()
    {
        Assert.Null(WorkerScratchBudget.RequiredBytes(0, null, Image()));
        Assert.Null(WorkerScratchBudget.RequiredBytes(1, -1, Image()));
        Assert.Null(WorkerScratchBudget.RequiredBytes(long.MaxValue, null, null));
        Assert.Null(WorkerScratchBudget.RequiredBytes(long.MaxValue, long.MaxValue, Image()));
        Assert.Null(WorkerScratchBudget.RequiredBytes(1000, null, Image() with { SourceImage = null }));
        Assert.Equal(1500, WorkerScratchBudget.RequiredBytes(1000, 900, null));
    }
}
