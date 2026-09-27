using Optimisarr.Sidecar.Linux;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Core.Queue;

namespace Optimisarr.Sidecar.Linux.Tests;

public sealed class HostTests
{
    [Fact]
    public void A_second_worker_cannot_share_an_active_scratch_or_config_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new WorkerDirectoryLock(root))
                Assert.Throws<IOException>(() => new WorkerDirectoryLock(root));
            using var afterExit = new WorkerDirectoryLock(root);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("hevc_qsv")]
    [InlineData("hevc_vaapi")]
    [InlineData("hevc_nvenc")]
    public void Server_generated_GPU_commands_pass_the_worker_contract(string encoder)
    {
        var spec = new TranscodeSpec("{{input}}", "{{output}}.mkv", "hevc", 24, null, false);
        foreach (var decode in new[] { false, true })
            Assert.Null(AssignmentCommand.Refuse(FfmpegCommandBuilder.Build(spec,
                videoEncoder: encoder, hardwareDecode: decode), "mkv", allowLinuxDevices: true));
    }

    [Fact]
    public void Decode_probe_uses_an_eight_bit_420_fixture_supported_by_hardware()
    {
        Assert.Contains("yuv420p", ProbeCommands.HardwareDecodeEncodeStep("clip.mp4", "libx264"));
    }

    [Fact]
    public async Task Linux_uses_the_measurement_binary_for_real_VMAF_proof()
    {
        var runner = new MeasurementRunner();
        var result = await new CapabilityProber(runner, "linux", "measurement", true)
            .ProbeAsync("linux-test", "encoding", 100, 1);
        Assert.Equal("linux", result.OperatingSystem);
        Assert.Contains("hevc_vaapi", result.VideoEncoders);
        Assert.Equal(VmafCapability.Cpu, result.Vmaf);
        Assert.True(runner.Scored);
    }

    private sealed class MeasurementRunner : ICommandRunner
    {
        public bool Scored { get; private set; }
        public Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
        {
            if (args.Contains("-encoders")) return Task.FromResult((0, " V....D libx264 H264\n V....D hevc_vaapi HEVC"));
            if (args.Contains("-hwaccels")) return Task.FromResult((0, ""));
            if (args.Contains("-filters")) return Task.FromResult((executable == "measurement" ? 0 : 1, "libvmaf"));
            if (args.Any(a => a.Contains("libvmaf")))
            {
                Scored = executable == "measurement";
                return Task.FromResult((Scored ? 0 : 1, ""));
            }
            return Task.FromResult((executable == "encoding" ? 0 : 1, ""));
        }
    }

    [Fact]
    public void Invalid_concurrency_and_server_urls_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorkerOptions.Read(key => key == "OPTIMISARR_CONCURRENCY" ? "0" : null));
        Assert.Throws<ArgumentException>(() => WorkerOptions.Read(key => key == "OPTIMISARR_SERVER" ? "file:///secret" : null));
    }

    [Fact]
    public void An_empty_server_setting_means_pair_from_the_dashboard() =>
        Assert.Null(WorkerOptions.Read(key => key == "OPTIMISARR_SERVER" ? "" : null).Server);

    [Fact]
    public void State_survives_restart_and_can_be_cleared_without_logging_the_credential()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileCredentialStore(root);
            var pairing = new StoredPairing("https://example.test", "secret", 12);
            store.Save(pairing);
            Assert.Equal(pairing, new FileCredentialStore(root).Load());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "pairing.json")));
            store.Clear();
            Assert.Null(store.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("cuda", "cuda")]
    [InlineData("qsv", "qsv")]
    public void Commands_with_hardware_surfaces_are_accepted(string decoder, string format)
    {
        Assert.Null(AssignmentCommand.Refuse(["-hwaccel", decoder, "-hwaccel_output_format", format,
            "-i", "{{input}}", "-c:v", "hevc_qsv", "{{output}}.mkv"], "mkv"));
    }

    [Fact]
    public void Linux_device_options_accept_only_the_fixed_render_node_and_known_device_name()
    {
        Assert.Null(AssignmentCommand.Refuse(["-vaapi_device", "/dev/dri/renderD128", "-i", "{{input}}",
            "-c:v", "hevc_vaapi", "{{output}}.mkv"], "mkv", allowLinuxDevices: true));
        Assert.NotNull(AssignmentCommand.Refuse(["-vaapi_device", "/etc/passwd", "-i", "{{input}}",
            "{{output}}.mkv"], "mkv", allowLinuxDevices: true));
        Assert.NotNull(AssignmentCommand.Refuse(["-init_hw_device", "qsv=hw,child_device=/secret", "-i", "{{input}}",
            "{{output}}.mkv"], "mkv"));
    }
}
