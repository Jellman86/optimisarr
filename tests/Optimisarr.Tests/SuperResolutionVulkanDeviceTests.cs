using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

public sealed class SuperResolutionVulkanDeviceTests
{
    private const string IntelAndLlvmpipeOutput = """
        [0 Intel(R) Graphics (ARL)]  queueC=0[1]  queueG=0[1]  queueT=0[1]
        [0 Intel(R) Graphics (ARL)]  bugsbn1=0  bugbilz=0  bugcopc=0  bugihfa=0
        [0 Intel(R) Graphics (ARL)]  fp16-p/s/a=1/1/1  int8-p/s/a=1/1/1
        [0 Intel(R) Graphics (ARL)]  subgroup=32  basic=1  vote=1  ballot=1  shuffle=1
        [1 llvmpipe (LLVM 20.1.2, 256 bits)]  queueC=0[1]  queueG=0[1]  queueT=0[1]
        [1 llvmpipe (LLVM 20.1.2, 256 bits)]  bugsbn1=0  bugbilz=0  bugcopc=0  bugihfa=0
        """;

    private const string LlvmpipeOnlyOutput = """
        [0 llvmpipe (LLVM 20.1.2, 256 bits)]  queueC=0[1]  queueG=0[1]  queueT=0[1]
        [0 llvmpipe (LLVM 20.1.2, 256 bits)]  bugsbn1=0  bugbilz=0  bugcopc=0  bugihfa=0
        0.00%
        """;

    [Fact]
    public void ParseDevices_reads_each_unique_vulkan_device_from_ncnn_output()
    {
        var devices = SuperResolutionVulkanDevice.ParseDevices(IntelAndLlvmpipeOutput);

        Assert.Equal(2, devices.Count);
        Assert.Equal(new SuperResolutionVulkanDevice(0, "Intel(R) Graphics (ARL)", IsSoftware: false), devices[0]);
        Assert.Equal(new SuperResolutionVulkanDevice(1, "llvmpipe (LLVM 20.1.2, 256 bits)", IsSoftware: true), devices[1]);
    }

    [Theory]
    [InlineData("llvmpipe (LLVM 20.1.2, 256 bits)", true)]
    [InlineData("Mesa Lavapipe", true)]
    [InlineData("SwiftShader Device (Subzero)", true)]
    [InlineData("Microsoft Basic Render Driver", true)]
    [InlineData("NVIDIA GeForce GTX 1050", false)]
    [InlineData("Intel(R) Graphics (ARL)", false)]
    [InlineData("AMD Radeon RX 6600 (RADV NAVI23)", false)]
    public void IsSoftwareRenderer_distinguishes_software_vulkan_from_hardware_gpus(string name, bool expectedSoftware)
    {
        Assert.Equal(expectedSoftware, SuperResolutionVulkanDevice.IsSoftwareRenderer(name));
    }

    [Fact]
    public void ResolveSelectedDevice_picks_index_zero_for_auto_explicit_index_for_gpu_and_null_for_cpu()
    {
        var devices = SuperResolutionVulkanDevice.ParseDevices(IntelAndLlvmpipeOutput);

        var autoDevice = SuperResolutionVulkanDevice.ResolveSelectedDevice(devices, SuperResolutionOptions.Defaults with { Device = "auto" });
        var gpu1 = SuperResolutionVulkanDevice.ResolveSelectedDevice(devices, SuperResolutionOptions.Defaults with { Device = "gpu:1" });
        var cpu = SuperResolutionVulkanDevice.ResolveSelectedDevice(devices, SuperResolutionOptions.Defaults with { Device = "cpu" });

        Assert.Equal(0, autoDevice?.Index);
        Assert.Equal(1, gpu1?.Index);
        Assert.Null(cpu);
    }

    [Fact]
    public void SoftwareFallbackWarning_warns_with_actionable_fix_when_auto_or_gpu_lands_on_software_vulkan()
    {
        var devices = SuperResolutionVulkanDevice.ParseDevices(LlvmpipeOnlyOutput);
        var autoOptions = SuperResolutionOptions.Defaults with { Device = "auto" };
        var selected = SuperResolutionVulkanDevice.ResolveSelectedDevice(devices, autoOptions);

        var warning = SuperResolutionVulkanDevice.SoftwareFallbackWarning(selected, autoOptions);

        Assert.NotNull(warning);
        Assert.Contains("llvmpipe", warning);
        Assert.Contains("NVIDIA_DRIVER_CAPABILITIES", warning);
        Assert.Contains("graphics", warning);
        Assert.Contains("/dev/dri", warning);
    }

    [Fact]
    public void SoftwareFallbackWarning_is_null_when_hardware_gpu_is_selected_or_cpu_was_requested()
    {
        var devices = SuperResolutionVulkanDevice.ParseDevices(IntelAndLlvmpipeOutput);

        var hardware = SuperResolutionVulkanDevice.ResolveSelectedDevice(devices, SuperResolutionOptions.Defaults with { Device = "auto" });
        Assert.Null(SuperResolutionVulkanDevice.SoftwareFallbackWarning(hardware, SuperResolutionOptions.Defaults with { Device = "auto" }));

        var cpuOptions = SuperResolutionOptions.Defaults with { Device = "cpu" };
        var llvmpipe = new SuperResolutionVulkanDevice(0, "llvmpipe (LLVM 20.1.2, 256 bits)", IsSoftware: true);
        Assert.Null(SuperResolutionVulkanDevice.SoftwareFallbackWarning(llvmpipe, cpuOptions));
    }
}
