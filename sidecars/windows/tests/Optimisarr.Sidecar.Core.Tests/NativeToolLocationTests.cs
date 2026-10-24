using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeToolLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-path-" + Guid.NewGuid().ToString("N"));
    public NativeToolLocationTests() { Directory.CreateDirectory(Path.Combine(_root, "package")); Directory.CreateDirectory(Path.Combine(_root, "system")); }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Metadata_uses_the_system_package_when_it_is_not_bundled_beside_FFmpeg()
    {
        var tool = Path.Combine(_root, "system", "exiftool" + (OperatingSystem.IsWindows() ? ".exe" : "")); File.WriteAllText(tool, "fixture");
        Assert.Equal(tool, NativeToolLocation.Resolve("exiftool", Path.Combine(_root, "package"), null, Path.Combine(_root, "system")));
    }

    [Fact]
    public void An_explicit_missing_override_cannot_silently_use_another_installed_tool()
    {
        var name = "exiftool" + (OperatingSystem.IsWindows() ? ".exe" : "");
        File.WriteAllText(Path.Combine(_root, "package", name), "fixture");
        var requested = Path.Combine(_root, "missing", name);
        Assert.Equal(requested, NativeToolLocation.Resolve("exiftool", Path.Combine(_root, "package"), requested, null));
    }

    [Fact]
    public void Bundled_tools_win_over_PATH_and_command_overrides_are_resolved_to_hashable_files()
    {
        var name = "exiftool" + (OperatingSystem.IsWindows() ? ".exe" : "");
        var bundled = Path.Combine(_root, "package", name); File.WriteAllText(bundled, "bundle");
        var system = Path.Combine(_root, "system", name); File.WriteAllText(system, "system");
        Assert.Equal(bundled, NativeToolLocation.Resolve("exiftool", Path.Combine(_root, "package"), null, Path.Combine(_root, "system")));
        Assert.Equal(system, NativeToolLocation.Resolve("exiftool", Path.Combine(_root, "package"), "exiftool", Path.Combine(_root, "system")));
    }
}
