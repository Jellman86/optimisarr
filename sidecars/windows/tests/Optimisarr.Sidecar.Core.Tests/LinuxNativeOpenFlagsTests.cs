using System.Runtime.InteropServices;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class LinuxNativeOpenFlagsTests
{
    [Theory]
    [InlineData(Architecture.X64, 0x10000, 0x20000)]
    [InlineData(Architecture.Arm64, 0x4000, 0x8000)]
    public void Directory_and_no_follow_flags_match_the_target_linux_abi(Architecture architecture, int directory, int noFollow)
    {
        var flags = LinuxNativeOpenFlags.For(architecture);
        Assert.Equal(directory, flags.Directory); Assert.Equal(noFollow, flags.NoFollow);
        Assert.Equal(0, flags.Directory & flags.NoFollow);
        Assert.Equal(LinuxNativeOpenFlags.For(RuntimeInformation.ProcessArchitecture), LinuxNativeOpenFlags.Current);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.S390x)]
    public void An_unverified_abi_cannot_silently_use_another_architectures_safety_flags(Architecture architecture) =>
        Assert.Throws<PlatformNotSupportedException>(() => LinuxNativeOpenFlags.For(architecture));
}
