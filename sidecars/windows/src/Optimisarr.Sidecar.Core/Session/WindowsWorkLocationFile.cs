using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Reads the work-location override file, judging it on the one handle its text is read through.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsWorkLocationFile
{
    private const int SharingAttempts = 5;
    private static readonly TimeSpan SharingRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The override file's one line, or why it must not be used. Absent means not configured; any
    /// other failure is a problem, never a silent fall-through to the next setting.
    ///
    /// <para>One handle, opened without following a link and without sharing writes, carries every
    /// check and then the read: whether it is a link, how many names it has, its size, its owner and
    /// access, and finally its text. Nothing can be swapped in between, because nothing is looked up
    /// by name a second time.</para>
    ///
    /// <para>Any account may read the file, and one that opens it sharing nothing keeps this open
    /// from succeeding. That cannot be prevented, so the open is retried briefly and then fails
    /// closed with a plain reason: no work is taken rather than work going somewhere unintended.</para>
    /// </summary>
    public static (string? Configured, string? Problem) Read(string path) => Read(path, Thread.Sleep);

    /// <param name="wait">Waits between attempts while another program holds the file.</param>
    internal static (string? Configured, string? Problem) Read(string path, Action<TimeSpan> wait)
    {
        SafeFileHandle? handle = null;
        var error = 0;
        for (var attempt = 1; attempt <= SharingAttempts; attempt++)
        {
            handle = WindowsFileApi.TryOpen(path, WindowsFileApi.GenericRead | WindowsFileApi.ReadControl, WindowsFileApi.ShareRead, out error);
            if (handle is not null || error != WindowsFileApi.ErrorSharingViolation) break;
            if (attempt < SharingAttempts) wait(SharingRetryDelay);
        }

        if (handle is null)
        {
            return WindowsFileApi.IsMissing(error) ? (null, null)
                : error == WindowsFileApi.ErrorSharingViolation
                    ? (null, "The work-location file is held open by another program, so it could not be read and no work is taken. "
                        + "Close that program and restart the service.")
                    : (null, $"The work-location file could not be read ({WindowsFileApi.Describe(error)}).");
        }

        using (handle)
        {
            try
            {
                var identity = WindowsFileApi.Identify(handle);
                if (WorkLocation.OverrideFileShapeProblem(identity.IsLink, identity.IsDirectory, identity.Links) is { } shape)
                {
                    return (null, shape);
                }

                if (identity.Size > WorkLocation.MaximumOverrideFileBytes)
                {
                    return (null, "The work-location file is far longer than one folder path, so it was not read.");
                }

                var (owner, access) = WindowsFileApi.ReadAccess(handle);
                if (WorkLocation.OverrideFileTrustProblem(owner, access ?? [new AccessEntry(WorkAccess.Everyone, 0x1F01FF)]) is { } untrusted)
                {
                    return (null, untrusted);
                }

                using var stream = new FileStream(handle, FileAccess.Read);
                var bytes = new byte[identity.Size];
                stream.ReadExactly(bytes);
                return WorkLocation.ReadOverrideBytes(bytes);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                return (null, $"The work-location file could not be read ({failure.Message}).");
            }
        }
    }
}
