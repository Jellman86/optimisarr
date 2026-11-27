using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.SuperResolution;

/// <summary>One Vulkan device advertised by an ncnn-vulkan engine on stderr.</summary>
public sealed partial record SuperResolutionVulkanDevice(int Index, string Name, bool IsSoftware)
{
    private static readonly string[] SoftwareMarkers =
    [
        "llvmpipe",
        "lavapipe",
        "swiftshader",
        "softpipe",
        "microsoft basic render driver",
    ];

    [GeneratedRegex(@"^\s*\[(\d+)\s+([^\]]+)\]")]
    private static partial Regex DeviceLinePattern();

    /// <summary>
    /// Reads each unique Vulkan device from ncnn-vulkan output lines such as
    /// <c>[0 Intel(R) Graphics (ARL)]  queueC=0[1] ...</c>.
    /// </summary>
    public static IReadOnlyList<SuperResolutionVulkanDevice> ParseDevices(string? engineOutput)
    {
        if (string.IsNullOrWhiteSpace(engineOutput))
        {
            return [];
        }

        var devices = new List<SuperResolutionVulkanDevice>();
        var seen = new HashSet<int>();
        foreach (var rawLine in engineOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DeviceLinePattern().Match(rawLine);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !seen.Add(index))
            {
                continue;
            }

            var name = match.Groups[2].Value.Trim();
            if (name.Length > 0)
            {
                devices.Add(new SuperResolutionVulkanDevice(index, name, IsSoftwareRenderer(name)));
            }
        }

        return devices;
    }

    public static bool IsSoftwareRenderer(string deviceName) =>
        SoftwareMarkers.Any(marker => deviceName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the Vulkan device the engine uses for <paramref name="options"/>, or null when
    /// explicit CPU mode was requested or no matching Vulkan device was listed.
    /// </summary>
    public static SuperResolutionVulkanDevice? ResolveSelectedDevice(
        IReadOnlyList<SuperResolutionVulkanDevice> devices, SuperResolutionOptions options)
    {
        if (devices.Count == 0 || options.Device == "cpu")
        {
            return null;
        }

        if (options.GpuIndex is { } index)
        {
            return devices.FirstOrDefault(device => device.Index == index);
        }

        // ncnn-vulkan's automatic mode prefers hardware GPUs over software CPU renderers such as llvmpipe.
        return devices.FirstOrDefault(device => !device.IsSoftware) ?? devices[0];
    }

    /// <summary>
    /// Returns a plain warning when automatic or GPU mode silently lands on a CPU software Vulkan
    /// renderer such as Mesa llvmpipe, which passes a 64x64 probe but takes hours or days on real
    /// video. Explicit CPU mode returns null because the operator asked for it.
    /// </summary>
    public static string? SoftwareFallbackWarning(
        SuperResolutionVulkanDevice? selected, SuperResolutionOptions options)
    {
        if (options.Device == "cpu" || selected is not { IsSoftware: true })
        {
            return null;
        }

        return $"Upscaling ran on software Vulkan ({selected.Name}) on the CPU instead of a GPU, " +
            "which is far too slow for full videos. On NVIDIA in Docker, add 'graphics' to " +
            "NVIDIA_DRIVER_CAPABILITIES (compute,video,utility,graphics); on Intel or AMD, map /dev/dri.";
    }
}
