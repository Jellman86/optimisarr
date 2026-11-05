using System.Runtime.InteropServices;

namespace Optimisarr.Api.Replacement;

internal enum RenameOutcome { Renamed, DestinationExists, CrossDevice, Unsupported }

/// <summary>
/// A rename that can only ever be a rename. .NET's <see cref="File.Move(string, string)"/> quietly
/// copies and then deletes the source when the kernel answers EXDEV, which it does between separate
/// bind mounts even on one filesystem, so a caller cannot tell a rename from an unverified copy.
/// This never copies and never overwrites: it renames, or says why it did not.
/// </summary>
internal static class RenameOnly
{
    private const int AtFdCwd = -100;
    private const uint RenameNoReplace = 1;
    private const uint RenameExcl = 4;
    private const int Eperm = 1, Eacces = 13, Eexist = 17, Exdev = 18, Einval = 22, Enosys = 38, EnotsupLinux = 95, EnotsupDarwin = 45;

    public static RenameOutcome Rename(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return RenameLinux(source, destination);
            if (OperatingSystem.IsMacOS())
                return RenameX(source, destination, RenameExcl) == 0 ? RenameOutcome.Renamed : Interpret(Marshal.GetLastPInvokeError(), source);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return RenameOutcome.Unsupported;
        }

        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
            return RenameOutcome.CrossDevice;
        File.Move(source, destination);
        return RenameOutcome.Renamed;
    }

    private static RenameOutcome RenameLinux(string source, string destination)
    {
        if (RenameAt2(AtFdCwd, source, AtFdCwd, destination, RenameNoReplace) == 0) return RenameOutcome.Renamed;
        var error = Marshal.GetLastPInvokeError();
        if (error is not (Einval or Enosys or EnotsupLinux)) return Interpret(error, source);

        // Some filesystems reject the no-replace flag. A hard link also refuses an existing name
        // and also fails across mounts, so link then unlink is the same promise.
        if (Link(source, destination) != 0) return Interpret(Marshal.GetLastPInvokeError(), source, linking: true);
        if (Unlink(source) == 0) return RenameOutcome.Renamed;
        var unlinkError = Marshal.GetLastPInvokeError();
        Unlink(destination);
        return Interpret(unlinkError, source);
    }

    private static RenameOutcome Interpret(int error, string source, bool linking = false) => error switch
    {
        Eexist => RenameOutcome.DestinationExists,
        Exdev => RenameOutcome.CrossDevice,
        Einval or Enosys or EnotsupLinux or EnotsupDarwin => RenameOutcome.Unsupported,
        // A filesystem without hard links answers EPERM to link; it says nothing about permission.
        Eperm when linking => RenameOutcome.Unsupported,
        Eacces or Eperm => throw new UnauthorizedAccessException($"Permission denied moving {source}."),
        _ => throw new IOException($"Could not rename {source}: {Marshal.GetPInvokeErrorMessage(error)}."),
    };

    [DllImport("libc", EntryPoint = "renameat2", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int RenameX(string oldPath, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(string existing, string created);

    [DllImport("libc", EntryPoint = "unlink", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Unlink(string path);
}
