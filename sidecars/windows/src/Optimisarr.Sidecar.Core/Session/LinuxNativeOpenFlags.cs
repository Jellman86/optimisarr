using System.Runtime.InteropServices;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Linux <c>openat</c> flags whose numeric values differ between the x86-64 and
/// arm64 ABIs. Using one architecture's values on the other would silently drop
/// the directory / no-follow safety checks, so unverified ABIs fail closed.
/// </summary>
internal sealed record LinuxNativeOpenFlags(int Directory, int NoFollow)
{
    public static LinuxNativeOpenFlags Current => For(RuntimeInformation.ProcessArchitecture);

    public static LinuxNativeOpenFlags For(Architecture architecture) => architecture switch
    {
        // asm-generic: O_DIRECTORY 0200000, O_NOFOLLOW 0400000.
        Architecture.X64 => new(0x10000, 0x20000),
        // arm64: O_DIRECTORY 040000, O_NOFOLLOW 0100000.
        Architecture.Arm64 => new(0x4000, 0x8000),
        _ => throw new PlatformNotSupportedException(
            $"Native Linux accounting has no verified open flags for {architecture}."),
    };
}
