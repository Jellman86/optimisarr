using System.Runtime.InteropServices;

namespace Optimisarr.Core.SuperResolution;

/// <summary>Where the engine stands, as far as static inspection can tell.</summary>
public enum RealCuganReadiness
{
    /// <summary>No executable was found at the configured path or on PATH.</summary>
    NotInstalled,
    /// <summary>The saved options are not a valid combination.</summary>
    Misconfigured,
    /// <summary>Binary and models exist, but the Vulkan loader library cannot be loaded.</summary>
    VulkanUnavailable,
    /// <summary>Binary, models and loader all check out. A real run is only proven by a probe.</summary>
    Ready,
}

/// <summary>
/// The result of checking the engine in place: which binary was resolved, whether the model
/// files the configured combination needs exist, and whether the Vulkan loader library loads.
/// None of this claims a working GPU — only a real probe run can.
/// </summary>
public sealed record RealCuganDetection(
    string Engine,
    bool BinaryFound,
    string? BinaryPath,
    bool ModelsFound,
    string? ModelsRoot,
    string? ModelDirectory,
    string? MissingModelFile,
    bool VulkanLoaderAvailable,
    IReadOnlyList<string> Problems,
    RealCuganReadiness Readiness);

/// <summary>The outcome of a real small-sample engine run.</summary>
public sealed record RealCuganProbeResult(
    bool Success,
    int? ExitCode,
    string? Error,
    string? OutputTail,
    string? DeviceUsed);

/// <summary>
/// Checks the Real-CUGAN engine's availability on this machine. The static check resolves the
/// executable (a configured path wins, otherwise PATH), the model files the configured
/// combination loads, and the Vulkan loader. <see cref="ProbeAsync"/> then runs a real 64x64
/// frame through the configured device — the only check that proves the runtime actually works.
/// </summary>
public sealed class RealCuganDetectionService(SuperResolutionOptions options)
{
    public const string BinaryName = "realcugan-ncnn-vulkan";

    /// <summary>The engine executable: a configured path, else PATH (plus the Windows suffix).</summary>
    public static string ResolveBinary(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = OperatingSystem.IsWindows()
            ? [BinaryName + ".exe", BinaryName]
            : new[] { BinaryName };
        foreach (var directory in pathDirectories)
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        // Unresolved: report the bare name so the detection result explains what it looked for.
        return BinaryName;
    }

    /// <summary>
    /// The model root: a configured directory, else the release layout — the model directories
    /// (models-se/models-pro/models-nose) sit directly beside the binary. Null when that
    /// directory does not exist; the caller reports it as missing models.
    /// </summary>
    public static string? ResolveModelsRoot(string? configuredPath, string binaryPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Directory.Exists(configuredPath.Trim()) ? configuredPath.Trim() : null;
        }

        var binaryDirectory = Path.GetDirectoryName(Path.GetFullPath(binaryPath));
        if (string.IsNullOrEmpty(binaryDirectory))
        {
            return null;
        }

        return Directory.Exists(binaryDirectory) ? binaryDirectory : null;
    }

    public Task<RealCuganDetection> DetectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var problems = new List<string>();

        var configurationProblems = options.Validate();
        problems.AddRange(configurationProblems.Select(problem => problem.Message));

        var binaryPath = ResolveBinary(options.BinaryPath);
        var binaryFound = File.Exists(binaryPath) || (!Path.IsPathRooted(binaryPath) && WhereFinds(binaryPath));
        if (!binaryFound)
        {
            problems.Add(
                $"The {SuperResolutionOptions.EngineName} engine executable was not found" +
                (string.IsNullOrWhiteSpace(options.BinaryPath)
                    ? " on PATH. Install realcugan-ncnn-vulkan, or set its path in the super-resolution settings."
                    : $": '{options.BinaryPath}' does not exist."));
        }

        var modelsRoot = binaryFound ? ResolveModelsRoot(options.ModelsPath, binaryPath) : null;
        var modelDirectory = RealCuganModelCatalog.ModelDirectoryFor(options.Preset);
        var modelFile = RealCuganModelCatalog.ModelFileFor(options.Preset, options.Scale, options.NoiseLevel);
        var missingModel = (string?)null;
        var modelsFound = modelsRoot is not null
            && modelDirectory is not null
            && modelFile is not null
            && File.Exists(Path.Combine(modelsRoot, modelDirectory, modelFile + ".bin"))
            && File.Exists(Path.Combine(modelsRoot, modelDirectory, modelFile + ".param"));
        if (modelsRoot is null)
        {
            problems.Add(
                "The engine's model directory was not found. The release ships a 'models' directory " +
                "beside the binary; point the models path setting at it.");
        }
        else if (!modelsFound && modelDirectory is not null && modelFile is not null)
        {
            missingModel = $"{modelDirectory}/{modelFile}";
            problems.Add(
                $"The configured combination needs {missingModel}.param/.bin, which is not present under '{modelsRoot}'.");
        }

        var vulkanAvailable = VulkanLoaderAvailable();
        if (!vulkanAvailable)
        {
            problems.Add(
                "The Vulkan loader library could not be loaded. Install the Vulkan loader " +
                "(libvulkan.so.1) and a driver with a Vulkan ICD; the engine needs a Vulkan runtime even on the CPU path.");
        }

        var readiness = !binaryFound
            ? RealCuganReadiness.NotInstalled
            : configurationProblems.Count > 0
                ? RealCuganReadiness.Misconfigured
                : !vulkanAvailable
                    ? RealCuganReadiness.VulkanUnavailable
                    : RealCuganReadiness.Ready;

        return Task.FromResult(new RealCuganDetection(
            SuperResolutionOptions.EngineName,
            binaryFound,
            binaryFound ? binaryPath : null,
            modelsFound,
            modelsRoot,
            modelDirectory,
            missingModel,
            vulkanAvailable,
            problems,
            readiness));
    }

    /// <summary>
    /// A real small-sample run: one synthetic 64x64 PNG through the configured engine, device
    /// and models. This is the check that proves the Vulkan runtime and the model files work —
    /// static detection alone never does. Requires an ffmpeg on the host to draw the sample.
    /// </summary>
    public async Task<RealCuganProbeResult> ProbeAsync(
        string ffmpegCommand, CancellationToken cancellationToken)
    {
        var detection = await DetectAsync(cancellationToken);
        if (detection.Readiness != RealCuganReadiness.Ready)
        {
            return new RealCuganProbeResult(false, null,
                "Probe skipped: " + string.Join(" ", detection.Problems), null, options.Device);
        }

        var scratch = Path.Combine(Path.GetTempPath(), "optimisarr-realcugan-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var input = Path.Combine(scratch, "probe-in.png");
            var output = Path.Combine(scratch, "probe-out.png");
            var draw = await Tools.BoundedToolProcess.RunAsync(
                ffmpegCommand,
                ["-f", "lavfi", "-i", "color=c=gray:s=64x64", "-frames:v", "1", "-y", input],
                cancellationToken,
                timeout: TimeSpan.FromMinutes(1));
            if (draw.ExitCode != 0 || !File.Exists(input))
            {
                return new RealCuganProbeResult(false, draw.ExitCode,
                    "Could not draw the probe sample with ffmpeg.", draw.Error, options.Device);
            }

            var arguments = RealCuganCommandBuilder.BuildEngineArguments(
                input, output, detection.ModelsRoot!, options);
            var run = await Tools.BoundedToolProcess.RunAsync(
                detection.BinaryPath!, arguments, cancellationToken,
                timeout: TimeSpan.FromMinutes(5));
            var tail = string.Join("\n", new[] { run.Output, run.Error }.Where(text => !string.IsNullOrEmpty(text)));
            if (run.ExitCode != 0 || !File.Exists(output))
            {
                return new RealCuganProbeResult(false, run.ExitCode,
                    $"The engine exited {run.ExitCode} without producing output.", tail, options.Device);
            }

            return new RealCuganProbeResult(true, run.ExitCode, null, tail, options.Device);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* the probe scratch is disposable; a straggler file is harmless */ }
        }
    }

    private static bool VulkanLoaderAvailable()
    {
        var loader = OperatingSystem.IsWindows() ? "vulkan-1.dll"
            : OperatingSystem.IsMacOS() ? "libMoltenVK.dylib"
            : "libvulkan.so.1";
        return NativeLibrary.TryLoad(loader, out _);
    }

    private static bool WhereFinds(string command)
    {
        // A bare relative name can also sit in the current working directory (e.g. a manual install).
        return File.Exists(command);
    }
}
