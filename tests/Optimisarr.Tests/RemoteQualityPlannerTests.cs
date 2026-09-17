using Optimisarr.Api.Workers;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemoteQualityPlannerTests
{
    [Fact]
    public void Sampled_commands_seek_on_the_reference_grid_and_leave_the_distorted_lead_to_the_worker()
    {
        var policy = VerificationPolicy.Default with { QualityGateEnabled = true, ClipVmafEnabled = true };

        var contract = RemoteQualityPlanner.Plan(
            policy,
            referenceWidth: 1920,
            referenceHeight: 1080,
            referenceIsHdr: false,
            hdrConvertedToSdr: false,
            durationSeconds: 1389.638,
            referenceFrameRate: 24000d / 1001d,
            referenceContainerLeadSeconds: 0.021,
            crop: null,
            decimation: null);

        Assert.NotNull(contract);
        Assert.Equal(3, contract!.WindowCount);
        var first = contract.Commands[0];
        // The worker has both files once it has encoded, so it is the one that can measure how far
        // into its container the candidate's first picture sits. The server leaves it that token.
        var filter = first[first.ToList().IndexOf("-lavfi") + 1];
        Assert.Contains($"[0:v]settb=AVTB,setpts=PTS-{RemoteQualityContract.DistortedShiftPlaceholder}*1000000,fps=", filter);
        Assert.Single(filter.Split(RemoteQualityContract.DistortedShiftPlaceholder).Skip(1));
        // The reference's lead is the server's to know, so the seek is already on its frame grid.
        Assert.Equal("113.008875", first[first.ToList().IndexOf("-ss") + 1]);
    }

    [Fact]
    public void An_unknown_reference_lead_still_plans_but_keeps_the_whole_second_seek()
    {
        var policy = VerificationPolicy.Default with { QualityGateEnabled = true, ClipVmafEnabled = true };

        var contract = RemoteQualityPlanner.Plan(
            policy, 1920, 1080, false, false, 1389.638, 24000d / 1001d, null, null, null);

        Assert.NotNull(contract);
        var first = contract!.Commands[0];
        Assert.Equal("113", first[first.ToList().IndexOf("-ss") + 1]);
        Assert.Contains(RemoteQualityContract.DistortedShiftPlaceholder, first[first.ToList().IndexOf("-lavfi") + 1]);
    }
}
