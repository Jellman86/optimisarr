using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>The engine invocation passes exactly the flags the realcugan-ncnn-vulkan CLI documents.</summary>
public sealed class RealCuganEngineTests
{
    private static readonly SuperResolutionOptions Options = SuperResolutionOptions.Defaults;
    private static readonly ISuperResolutionEngine Engine = RealCuganEngine.Instance;

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static string[] Pair(IReadOnlyList<string> args, string flag) => [flag, After(args, flag)];

    [Fact]
    public void The_settings_engine_is_real_cugan_under_its_recorded_name()
    {
        Assert.Same(Engine, Options.Engine);
        Assert.Equal(SuperResolutionOptions.EngineName, Engine.Name);
        Assert.Equal("realcugan-ncnn-vulkan", Engine.BinaryName);
    }

    [Fact]
    public void Models_live_beside_the_binary_in_the_release_layout() =>
        Assert.Equal("/opt/realcugan", Engine.ModelsRootBeside("/opt/realcugan"));

    [Fact]
    public void A_model_file_is_named_only_for_a_shipped_combination()
    {
        Assert.Equal("models-se", Engine.ModelDirectory("mixed"));
        Assert.Equal("up2x-denoise1x", Engine.ModelFile("mixed", 2, 1));
        Assert.Null(Engine.ModelFile("pro", 4, 0));
        Assert.Contains(Engine.ValidateModel("pro", 4, 0), error => error.Code == "superResolution.modelUnavailable");
        Assert.Empty(Engine.ValidateModel("mixed", 2, 1));
    }

    [Fact]
    public void Engine_arguments_pass_the_documented_flags_and_nothing_else()
    {
        var args = Engine.BuildArguments(
            "/work/1/in", "/work/1/out", "/models", Options);

        Assert.Equal(new[]
        {
            "-i", "/work/1/in",
            "-o", "/work/1/out",
            "-m", Path.Combine("/models", "models-se"),
            "-s", "2",
            "-n", "1",
            "-f", "png",
        }, args);
    }

    [Fact]
    public void Auto_device_omits_the_gpu_flag_letting_the_engine_choose()
    {
        var args = Engine.BuildArguments(
            "in", "out", "models", Options with { Device = "auto" });
        Assert.DoesNotContain("-g", args);
    }

    [Fact]
    public void Cpu_device_passes_minus_one_which_the_engine_documents_as_cpu()
    {
        var args = Engine.BuildArguments(
            "in", "out", "models", Options with { Device = "cpu" });
        Assert.Equal(new[] { "-g", "-1" }, Pair(args, "-g"));
    }

    [Fact]
    public void A_named_vulkan_device_passes_its_index()
    {
        var args = Engine.BuildArguments(
            "in", "out", "models", Options with { Device = "gpu:2" });
        Assert.Equal(new[] { "-g", "2" }, Pair(args, "-g"));
    }

    [Fact]
    public void The_model_directory_is_the_preset_mapping_under_the_configured_root()
    {
        var args = Engine.BuildArguments(
            "in", "out", "/opt/models", Options with { Preset = "pro" });
        Assert.Equal(Path.Combine("/opt/models", "models-pro"), After(args, "-m"));
    }

}
