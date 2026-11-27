using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>Links an ordinary, unelevated account can make, as an attacker on the machine would.</summary>
internal static class WindowsLinks
{
    /// <summary>A directory junction: needs no privilege, unlike a symbolic link.</summary>
    public static void Junction(string link, string target) => MakeLink("/J", link, target);

    /// <summary>A second name for an existing file on the same volume.</summary>
    public static void HardLink(string link, string target) => MakeLink("/H", link, target);

    /// <summary>
    /// Turns an existing, empty folder into a junction in place, as anyone allowed to write its
    /// attributes can, even while another process holds it open without delete sharing.
    /// </summary>
    public static void ConvertToJunction(string folder, string target)
    {
        const uint MountPointTag = 0xA0000003, SetReparsePoint = 0x000900A4, GenericWrite = 0x40000000, ShareAll = 7, OpenExisting = 3;
        const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        var print = Path.GetFullPath(target);
        var substitute = @"\??\" + print;
        var names = Encoding.Unicode.GetBytes(substitute + "\0" + print + "\0");
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(MountPointTag);
            writer.Write((ushort)(8 + names.Length));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)(substitute.Length * 2));
            writer.Write((ushort)((substitute.Length + 1) * 2));
            writer.Write((ushort)(print.Length * 2));
            writer.Write(names);
        }

        var buffer = data.ToArray();
        using var handle = CreateFile(folder, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{folder} could not be opened to convert it.");
        if (!DeviceIoControl(handle, SetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{folder} could not be turned into a junction.");
    }

    private static void MakeLink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", kind, link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"mklink {kind} failed: {output}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
