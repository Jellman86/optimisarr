using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Capability matching decides whether a job may be offered to a worker at all. It must fail
/// closed — an unproved capability is an unmet one — and every rejection must name its reason so
/// an operator can see why a paired sidecar is sitting idle.
/// </summary>
public class WorkerCapabilityMatcherTests
{
    private static WorkerCapabilities Capable() => new(
        OperatingSystem: "linux",
        Architecture: "x64",
        VideoEncoders: ["libx265", "hevc_nvenc"],
        HardwareDecoders: ["hevc_cuvid"],
        Vmaf: VmafCapability.Cuda,
        FreeScratchBytes: 100L * 1024 * 1024 * 1024,
        MaxConcurrency: 2);

    private static JobRequirements Wanted() => new(
        VideoEncoder: "libx265",
        HardwareDecoder: null,
        Vmaf: VmafCapability.Cpu,
        ScratchBytes: 10L * 1024 * 1024 * 1024);

    [Fact]
    public void Match_accepts_a_worker_that_satisfies_every_requirement()
    {
        var match = WorkerCapabilityMatcher.Match(Capable(), Wanted());

        Assert.True(match.Accepted);
        Assert.Empty(match.Reasons);
    }

    [Fact]
    public void Match_rejects_a_missing_video_encoder()
    {
        var match = WorkerCapabilityMatcher.Match(Capable(), Wanted() with { VideoEncoder = "av1_qsv" });

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("av1_qsv", StringComparison.Ordinal));
    }

    [Fact]
    public void Match_rejects_a_missing_hardware_decoder_when_one_is_required()
    {
        var match = WorkerCapabilityMatcher.Match(Capable(), Wanted() with { HardwareDecoder = "av1_qsv" });

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("av1_qsv", StringComparison.Ordinal));
    }

    [Fact]
    public void Match_allows_software_decode_when_no_hardware_decoder_is_required()
    {
        var noDecoders = Capable() with { HardwareDecoders = [] };

        var match = WorkerCapabilityMatcher.Match(noDecoders, Wanted());

        Assert.True(match.Accepted);
    }

    [Fact]
    public void Match_treats_cuda_vmaf_as_satisfying_a_cpu_requirement()
    {
        // CPU VMAF is the portable fallback, so a worker proving the CUDA backend can always
        // also score on the CPU. The reverse must not hold.
        var match = WorkerCapabilityMatcher.Match(Capable(), Wanted() with { Vmaf = VmafCapability.Cpu });

        Assert.True(match.Accepted);
    }

    [Fact]
    public void Match_rejects_a_cpu_only_worker_when_cuda_vmaf_is_required()
    {
        var cpuOnly = Capable() with { Vmaf = VmafCapability.Cpu };

        var match = WorkerCapabilityMatcher.Match(cpuOnly, Wanted() with { Vmaf = VmafCapability.Cuda });

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("VMAF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Match_rejects_a_worker_with_no_vmaf_when_scoring_is_required()
    {
        var noVmaf = Capable() with { Vmaf = VmafCapability.None };

        var match = WorkerCapabilityMatcher.Match(noVmaf, Wanted());

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("VMAF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Match_accepts_a_worker_with_no_vmaf_when_the_job_does_not_score()
    {
        var noVmaf = Capable() with { Vmaf = VmafCapability.None };

        var match = WorkerCapabilityMatcher.Match(noVmaf, Wanted() with { Vmaf = VmafCapability.None });

        Assert.True(match.Accepted);
    }

    [Fact]
    public void Match_rejects_insufficient_scratch_space()
    {
        var tiny = Capable() with { FreeScratchBytes = 1024 };

        var match = WorkerCapabilityMatcher.Match(tiny, Wanted());

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("scratch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Match_rejects_a_worker_that_accepts_no_concurrent_work()
    {
        // Drain/disable is expressed by dropping concurrency to zero, so this is the path an
        // operator takes to stop new assignments without unpairing the sidecar.
        var drained = Capable() with { MaxConcurrency = 0 };

        var match = WorkerCapabilityMatcher.Match(drained, Wanted());

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("concurren", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Match_reports_every_unmet_requirement_rather_than_only_the_first()
    {
        var poor = Capable() with
        {
            VideoEncoders = [],
            Vmaf = VmafCapability.None,
            FreeScratchBytes = 0,
            MaxConcurrency = 0
        };

        var match = WorkerCapabilityMatcher.Match(poor, Wanted());

        Assert.False(match.Accepted);
        Assert.Equal(4, match.Reasons.Count);
    }

    [Fact]
    public void Match_compares_encoder_names_case_insensitively()
    {
        var shouty = Capable() with { VideoEncoders = ["LIBX265"] };

        var match = WorkerCapabilityMatcher.Match(shouty, Wanted());

        Assert.True(match.Accepted);
    }

    [Fact]
    public void Match_rejects_a_worker_advertising_no_encoder_for_an_unnamed_requirement()
    {
        // An empty required encoder is a malformed assignment, not a wildcard. Fail closed.
        var match = WorkerCapabilityMatcher.Match(Capable(), Wanted() with { VideoEncoder = "  " });

        Assert.False(match.Accepted);
        Assert.Contains(match.Reasons, r => r.Contains("encoder", StringComparison.OrdinalIgnoreCase));
    }
}
