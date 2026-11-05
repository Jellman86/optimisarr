using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Optimisarr.Api.Replacement;

internal enum RenameOutcome { Renamed, DestinationExists, CrossDevice, Unsupported }

/// <summary>
/// A rename that can only ever be a rename. .NET's <see cref="File.Move(string, string)"/> quietly
/// copies and then deletes the source when the two paths are on different mounts or volumes, which
/// on Linux includes separate bind mounts of one filesystem, so a caller cannot tell a rename from
/// an unverified copy. This never copies and never overwrites: it renames, or says why it did not,
/// and every answer other than <see cref="RenameOutcome.Renamed"/> leaves both paths untouched.
/// </summary>
internal static class RenameOnly
{
    private const int AtFdCwd = -100;
    private const uint RenameNoReplace = 1;
    private const uint RenameExcl = 4;
    private const int Eperm = 1, Eacces = 13, Eexist = 17, Exdev = 18, Einval = 22, Enosys = 38, EnotsupLinux = 95, EnotsupDarwin = 45;
    private const int WindowsAccessDenied = 5, WindowsNotSameDevice = 17, WindowsFileExists = 80, WindowsAlreadyExists = 183;

    public static RenameOutcome Rename(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return MoveFileEx(source, destination, 0) ? RenameOutcome.Renamed : InterpretWindows(Marshal.GetLastPInvokeError(), source);
            var renamed = OperatingSystem.IsMacOS()
                ? RenameX(source, destination, RenameExcl)
                : RenameAt2(AtFdCwd, source, AtFdCwd, destination, RenameNoReplace);
            return renamed == 0 ? RenameOutcome.Renamed : Interpret(Marshal.GetLastPInvokeError(), source);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return RenameOutcome.Unsupported;
        }
    }

    private static RenameOutcome Interpret(int error, string source) => error switch
    {
        Eexist => RenameOutcome.DestinationExists,
        Exdev => RenameOutcome.CrossDevice,
        // A filesystem or kernel without no-replace rename. No fallback is attempted here: the
        // verified copy is slower, but it cannot lose a name the way link-then-unlink cleanup can.
        Einval or Enosys or EnotsupLinux or EnotsupDarwin => RenameOutcome.Unsupported,
        Eacces or Eperm => throw new UnauthorizedAccessException($"Permission denied moving {source}."),
        _ => throw new IOException($"Could not rename {source}: {Marshal.GetPInvokeErrorMessage(error)}."),
    };

    private static RenameOutcome InterpretWindows(int error, string source) => error switch
    {
        WindowsFileExists or WindowsAlreadyExists => RenameOutcome.DestinationExists,
        WindowsNotSameDevice => RenameOutcome.CrossDevice,
        WindowsAccessDenied => throw new UnauthorizedAccessException($"Permission denied moving {source}."),
        _ => throw new IOException($"Could not rename {source}: {new Win32Exception(error).Message}."),
    };

    [DllImport("libc", EntryPoint = "renameat2", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int RenameX(string oldPath, string newPath, uint flags);

    // Flags 0: no MOVEFILE_COPY_ALLOWED, so another volume is an error, and no replacing.
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
}
