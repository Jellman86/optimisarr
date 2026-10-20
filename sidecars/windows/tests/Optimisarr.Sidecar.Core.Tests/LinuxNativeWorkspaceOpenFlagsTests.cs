using System.Runtime.InteropServices;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class LinuxNativeWorkspaceOpenFlagsTests
{
    [Theory]
    [InlineData(Architecture.X64, 0xb0000)]
    [InlineData(Architecture.Arm64, 0x8c000)]
    public void Directory_opens_require_a_directory_refuse_links_and_do_not_leak_handles(Architecture architecture, int expected) =>
        Assert.Equal(expected, NativeWorkspaceDirectory.LinuxDirectoryOpenFlags(architecture));

    [Theory]
    [InlineData(Architecture.X64, FileMode.Open, false, false, 0xa0800)]
    [InlineData(Architecture.Arm64, FileMode.Open, false, false, 0x88800)]
    [InlineData(Architecture.X64, FileMode.Open, true, false, 0xa0802)]
    [InlineData(Architecture.Arm64, FileMode.Open, true, false, 0x88802)]
    [InlineData(Architecture.X64, FileMode.Open, false, true, 0x20800)]
    [InlineData(Architecture.Arm64, FileMode.Open, false, true, 0x8800)]
    [InlineData(Architecture.X64, FileMode.CreateNew, true, true, 0x208c2)]
    [InlineData(Architecture.Arm64, FileMode.CreateNew, true, true, 0x88c2)]
    [InlineData(Architecture.X64, FileMode.CreateNew, false, false, 0xa08c2)]
    [InlineData(Architecture.Arm64, FileMode.CreateNew, false, false, 0x888c2)]
    [InlineData(Architecture.X64, FileMode.OpenOrCreate, true, false, 0xa0842)]
    [InlineData(Architecture.Arm64, FileMode.OpenOrCreate, true, false, 0x88842)]
    public void File_opens_refuse_links_preserve_access_and_only_inherit_explicit_guards(
        Architecture architecture, FileMode mode, bool locked, bool inherit, int expected) =>
        Assert.Equal(expected, NativeWorkspaceDirectory.LinuxFileOpenFlags(architecture, mode, locked, inherit));

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.S390x)]
    public void Unsupported_linux_abis_cannot_open_scratch_with_unverified_safety_flags(Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(() => NativeWorkspaceDirectory.LinuxDirectoryOpenFlags(architecture));
        Assert.Throws<PlatformNotSupportedException>(() => NativeWorkspaceDirectory.LinuxFileOpenFlags(architecture,
            FileMode.CreateNew, locked: true, inherit: true));
    }
}
