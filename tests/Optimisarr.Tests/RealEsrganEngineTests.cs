using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

public sealed class RealEsrganEngineTests
{
    private static readonly RealEsrganEngine Engine = RealEsrganEngine.Instance;

    private static SuperResolutionOptions Options(string model, int scale, string device = "auto") =>
        SuperResolutionOptions.Defaults with { EngineId = Engine.Name, Preset = model, Scale = scale, NoiseLevel = 0, Device = device };

    [Theory]
    [InlineData(RealEsrganEngine.DrawnVideoModel, 2, "realesr-animevideov3-x2")]
    [InlineData(RealEsrganEngine.DrawnVideoModel, 3, "realesr-animevideov3-x3")]
    [InlineData(RealEsrganEngine.DrawnVideoModel, 4, "realesr-animevideov3-x4")]
    [InlineData(RealEsrganEngine.LiveActionModel, 4, "realesrgan-x4plus")]
    [InlineData(RealEsrganEngine.DrawnModel, 4, "realesrgan-x4plus-anime")]
    public void Each_model_names_the_file_the_engine_loads(string model, int scale, string file)
    {
        Assert.Equal(file, Engine.ModelFile(model, scale, 0));
        Assert.Equal("models", Engine.ModelDirectory(model));
        Assert.Empty(Engine.ValidateModel(model, scale, 0));
    }

    [Theory]
    [InlineData(RealEsrganEngine.LiveActionModel, 2)]
    [InlineData(RealEsrganEngine.LiveActionModel, 3)]
    [InlineData(RealEsrganEngine.DrawnModel, 2)]
    [InlineData(RealEsrganEngine.DrawnModel, 3)]
    public void A_fixed_4x_model_refuses_a_smaller_scale_because_the_engine_crops_it_and_still_exits_cleanly(string model, int scale)
    {
        Assert.Null(Engine.ModelFile(model, scale, 0));
        Assert.Equal("superResolution.modelUnavailable", Assert.Single(Engine.ValidateModel(model, scale, 0)).Code);
    }

    [Theory]
    [InlineData("realesrnet-x4plus", 4, "superResolution.preset")]
    [InlineData(RealEsrganEngine.DrawnVideoModel, 5, "superResolution.scale")]
    public void An_unknown_model_or_scale_is_refused(string model, int scale, string code) =>
        Assert.Contains(Engine.ValidateModel(model, scale, 0), error => error.Code == code);

    [Fact]
    public void The_command_names_the_model_and_its_folder_and_never_passes_a_denoise_level()
    {
        var args = Engine.BuildArguments("/in", "/out", "/opt/realesrgan", Options(RealEsrganEngine.LiveActionModel, 4));

        Assert.Equal(
            ["-i", "/in", "-o", "/out", "-n", "realesrgan-x4plus", "-s", "4", "-m", Path.Combine("/opt/realesrgan", "models"), "-f", "png"],
            args);
    }

    [Fact]
    public void A_chosen_gpu_is_passed_to_the_engine()
    {
        var args = Engine.BuildArguments("/in", "/out", "/opt/realesrgan", Options(RealEsrganEngine.DrawnVideoModel, 2, "gpu:1"));

        Assert.Equal("1", args[args.ToList().IndexOf("-g") + 1]);
    }

    [Fact]
    public void Options_refuse_the_cpu_because_this_engine_has_no_cpu_mode()
    {
        var errors = Options(RealEsrganEngine.DrawnVideoModel, 2, "cpu").Validate();

        Assert.Equal("superResolution.device", Assert.Single(errors).Code);
    }

    [Fact]
    public void Real_cugan_keeps_its_cpu_mode() =>
        Assert.Empty((SuperResolutionOptions.Defaults with { Device = "cpu" }).Validate());

    [Fact]
    public void Options_refuse_an_engine_they_do_not_know() =>
        Assert.Equal("superResolution.engine", Assert.Single((SuperResolutionOptions.Defaults with { EngineId = "waifu2x" }).Validate()).Code);

    [Fact]
    public void Options_use_the_engine_they_name()
    {
        Assert.Same(RealCuganEngine.Instance, SuperResolutionOptions.Defaults.Engine);
        Assert.Same(Engine, Options(RealEsrganEngine.DrawnVideoModel, 2).Engine);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("realesrgan", RealEsrganEngine.LiveActionModel, 4, true)]
    [InlineData("realesrgan", RealEsrganEngine.DrawnVideoModel, 2, true)]
    [InlineData("realesrgan", RealEsrganEngine.LiveActionModel, 2, false)]
    [InlineData("realesrgan", "nonsense", 4, false)]
    [InlineData("realesrgan", null, 4, false)]
    [InlineData("realesrgan", RealEsrganEngine.DrawnVideoModel, null, false)]
    [InlineData("waifu2x", "any", 2, false)]
    [InlineData(null, RealEsrganEngine.DrawnVideoModel, null, false)]
    [InlineData(null, null, 2, false)]
    public void A_librarys_choice_is_validated(string? engine, string? model, int? scale, bool valid) =>
        Assert.Equal(valid, SuperResolutionModelChoice.Validate(engine, model, scale) is null);

    [Fact]
    public void A_librarys_real_esrgan_choice_overrides_the_servers_real_cugan_settings()
    {
        var server = SuperResolutionOptions.Defaults with { Enabled = true, Device = "gpu:1", BinaryPath = "/cugan/bin", ModelsPath = "/cugan/models" };

        var options = server.ForEngine(new SuperResolutionEnginePaths("/esrgan/bin", "/esrgan/root"), "realesrgan", RealEsrganEngine.DrawnVideoModel, 3);

        Assert.Equal(server with
        {
            EngineId = "realesrgan", Preset = RealEsrganEngine.DrawnVideoModel, Scale = 3, NoiseLevel = 0,
            BinaryPath = "/esrgan/bin", ModelsPath = "/esrgan/root",
        }, options);
    }

    [Theory]
    [InlineData("realesrgan", RealEsrganEngine.LiveActionModel, 4, "realesrgan realesrgan-x4plus 4x")]
    [InlineData("realcugan", "mixed", 2, "realcugan mixed 2x")]
    public void The_run_is_described_for_the_jobs_evidence(string engine, string model, int scale, string expected) =>
        Assert.Equal(expected, (SuperResolutionOptions.Defaults with { EngineId = engine, Preset = model, Scale = scale }).RunDescription);

    [Fact]
    public void A_library_without_a_choice_uses_the_servers_real_cugan_settings()
    {
        var server = SuperResolutionOptions.Defaults with { Enabled = true, Scale = 3 };

        Assert.Equal(server, server.ForEngine(new SuperResolutionEnginePaths("/esrgan/bin", "/esrgan/root"), null, null, null));
    }

    [Fact]
    public async Task A_detection_finds_models_in_the_release_layout_beside_the_binary()
    {
        using var dir = new TempDirectory();
        var binary = Path.Combine(dir.Path, Engine.BinaryName);
        File.WriteAllText(binary, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(binary, UnixFileMode.UserExecute | UnixFileMode.UserRead);
        var models = Path.Combine(dir.Path, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "realesrgan-x4plus.bin"), "");
        File.WriteAllText(Path.Combine(models, "realesrgan-x4plus.param"), "");

        var detection = await new SuperResolutionDetectionService(
                Options(RealEsrganEngine.LiveActionModel, 4) with { BinaryPath = binary }, _ => true)
            .DetectAsync(CancellationToken.None);

        Assert.Equal(SuperResolutionReadiness.Ready, detection.Readiness);
        Assert.Equal("realesrgan", detection.Engine);
    }

    [PosixFact]
    public async Task A_sample_run_that_only_copied_the_picture_is_not_called_a_success()
    {
        using var dir = new TempDirectory();
        const string Png = """
            def png(path, w, h):
                import struct
                open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + struct.pack('>I', 13) + b'IHDR' + struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0) + b'\0' * 4)
            """;
        var ffmpeg = Script(dir, "ffmpeg", Png + "\nimport sys\npng(sys.argv[-1], 64, 64)\n");
        var engine = Script(dir, Engine.BinaryName, Png + "\nimport sys, shutil\nshutil.copy(sys.argv[sys.argv.index('-i') + 1], sys.argv[sys.argv.index('-o') + 1])\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "models"));
        foreach (var extension in new[] { ".bin", ".param" }) File.WriteAllText(Path.Combine(dir.Path, "models", "realesr-animevideov3-x2" + extension), "");

        var probe = await new SuperResolutionDetectionService(
                Options(RealEsrganEngine.DrawnVideoModel, 2) with { BinaryPath = engine }, _ => true)
            .ProbeAsync(ffmpeg, CancellationToken.None);

        Assert.False(probe.Success);
        Assert.Contains("128x128", probe.Error);
    }

    private static string Script(TempDirectory dir, string name, string body)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllText(path, TestPython.Shebang + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "esrgan-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
