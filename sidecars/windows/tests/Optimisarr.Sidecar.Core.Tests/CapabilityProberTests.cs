using Optimisarr.Sidecar.Core.Capabilities;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Capability probing decides what work this machine is offered. It must fail closed — an unproved
/// capability is an absent one — because the server schedules against these answers and a job sent
/// for a capability the machine cannot honour can only fail and come back.
/// </summary>
public class CapabilityProberTests
{
    private const string Listing = """
         Encoders:
          V..... = Video
          ------
         V....D libx264              libx264 H.264 / AVC
         V....D libx265              libx265 H.265 / HEVC
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder
         V....D hevc_nvenc           NVIDIA NVENC hevc encoder
         V....D h264_qsv             Intel Quick Sync Video H.264 encoder
         A....D aac                  AAC (Advanced Audio Coding)
         A....D libopus              libopus Opus
         A....D pcm_s16le            PCM signed 16-bit little-endian
        """;

    [Fact]
    public async Task A_listed_encoder_is_not_advertised_until_it_has_actually_encoded()
    {
        // The case this exists for: a build carrying NVENC on a machine with no NVIDIA card, or a
        // driver too old for the build. Both list perfectly well and fail on first open.
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, "Hardware acceleration methods:\ncuda\n"),
            ["-filters"] = (0, "libvmaf"),
            ["h264_nvenc"] = (1, "Cannot load nvcuda.dll"),
            ["hevc_nvenc"] = (1, "Cannot load nvcuda.dll"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Contains("libx264", capabilities.VideoEncoders);
        Assert.DoesNotContain("h264_nvenc", capabilities.VideoEncoders);
        Assert.DoesNotContain("hevc_nvenc", capabilities.VideoEncoders);
    }

    [Fact]
    public async Task A_working_hardware_encoder_is_advertised()
    {
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, "Hardware acceleration methods:\ncuda\n"),
            ["-filters"] = (0, "libvmaf"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Contains("hevc_nvenc", capabilities.VideoEncoders);
        Assert.Contains("h264_qsv", capabilities.VideoEncoders);
    }

    [Fact]
    public async Task An_audio_encoder_the_build_lacks_is_never_claimed()
    {
        // Optimisarr emits libopus for an Opus target. Claiming one the build cannot open means a
        // job handed over, failed with "Unknown encoder", and handed straight back.
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, ""),
            ["-filters"] = (0, "libvmaf"),
            ["libopus"] = (1, "Unknown encoder 'libopus'"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Contains("aac", capabilities.AudioEncoders);
        Assert.DoesNotContain("libopus", capabilities.AudioEncoders);
        Assert.DoesNotContain("pcm_s16le", capabilities.AudioEncoders);
    }

    [Fact]
    public async Task Hardware_decode_is_proved_by_a_round_trip_not_by_the_listing()
    {
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, "Hardware acceleration methods:\ncuda\nqsv\n"),
            ["-filters"] = (0, "libvmaf"),
            ["-hwaccel:qsv"] = (1, "Failed to open the QSV device"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Contains("cuda", capabilities.HardwareDecoders);
        Assert.DoesNotContain("qsv", capabilities.HardwareDecoders);
    }

    [Fact]
    public async Task Cuda_vmaf_is_claimed_only_after_scoring_something_on_the_gpu()
    {
        // The one capability that cannot be inferred: a build can carry libvmaf_cuda while the
        // machine has no usable CUDA device, and the server will send a GPU measurement command on
        // the strength of this answer alone.
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, "Hardware acceleration methods:\ncuda\n"),
            ["-filters"] = (0, "libvmaf\nlibvmaf_cuda"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Equal(VmafCapability.Cuda, capabilities.Vmaf);
    }

    [Fact]
    public async Task A_build_with_the_cuda_filter_but_no_usable_device_falls_back_to_cpu()
    {
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, Listing),
            ["-hwaccels"] = (0, "Hardware acceleration methods:\ncuda\n"),
            ["-filters"] = (0, "libvmaf\nlibvmaf_cuda"),
            ["libvmaf_cuda"] = (1, "No CUDA device available"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 2);

        Assert.Equal(VmafCapability.Cpu, capabilities.Vmaf);
    }

    [Fact]
    public async Task A_machine_with_no_ffmpeg_claims_nothing_and_no_capacity()
    {
        // Reporting capacity the server can never use makes a healthy-looking worker that never
        // takes anything, which is worse than one that plainly says it can do nothing.
        var capabilities = await new CapabilityProber(new ScriptedRunner()).ProbeAsync("PC", null, 500, 4);

        Assert.Empty(capabilities.VideoEncoders);
        Assert.Equal(VmafCapability.None, capabilities.Vmaf);
        Assert.Equal(0, capabilities.MaxConcurrency);
    }

    [Fact]
    public async Task No_working_encoder_means_no_concurrency_whatever_else_works()
    {
        var runner = new ScriptedRunner
        {
            ["-encoders"] = (0, " A....D aac  AAC"),
            ["-hwaccels"] = (0, "cuda"),
            ["-filters"] = (0, "libvmaf"),
        };

        var capabilities = await new CapabilityProber(runner).ProbeAsync("PC", "ffmpeg.exe", 500, 4);

        Assert.Equal(0, capabilities.MaxConcurrency);
        Assert.Contains("aac", capabilities.AudioEncoders);
    }
}

/// <summary>
/// Answers scripted per command, so the two-stage probe can be walked without FFmpeg. Anything not
/// scripted succeeds, so a test states only what it wants to fail.
/// </summary>
public sealed class ScriptedRunner : Dictionary<string, (int ExitCode, string Output)>, ICommandRunner
{
    public Task<(int ExitCode, string Output)> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        foreach (var listing in new[] { "-encoders", "-hwaccels", "-filters" })
        {
            if (arguments.Contains(listing))
            {
                return Task.FromResult(TryGet(listing) ?? (1, ""));
            }
        }

        // A measurement names its filter; check before the encoder, since it also names an input.
        var filter = arguments.SkipWhile(a => a != "-lavfi").Skip(1).FirstOrDefault();
        if (filter is not null)
        {
            var key = filter.Contains("libvmaf_cuda") ? "libvmaf_cuda" : "libvmaf";
            return Task.FromResult(TryGet(key) ?? (0, ""));
        }

        // A decode probe names its accelerator.
        var accelerator = arguments.SkipWhile(a => a != "-hwaccel").Skip(1).FirstOrDefault();
        if (accelerator is not null)
        {
            return Task.FromResult(TryGet($"-hwaccel:{accelerator}") ?? (0, ""));
        }

        foreach (var flag in new[] { "-c:v", "-c:a" })
        {
            var index = arguments.ToList().IndexOf(flag);
            if (index >= 0 && index + 1 < arguments.Count)
            {
                return Task.FromResult(TryGet(arguments[index + 1]) ?? (0, ""));
            }
        }

        return Task.FromResult((0, ""));
    }

    private (int, string)? TryGet(string key) => TryGetValue(key, out var value) ? value : null;
}
