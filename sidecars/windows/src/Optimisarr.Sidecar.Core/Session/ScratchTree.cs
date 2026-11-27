namespace Optimisarr.Sidecar.Core.Session;

public enum ScratchEntryKind { File, Directory, Link }

/// <summary>
/// One opened entry in a job's folder. It is opened without following it, so a junction or
/// symbolic link is seen as itself, and it stays held while it is inspected and removed, so it
/// cannot be swapped for a link in between. A folder's names come from the folder it holds, and
/// each is opened inside that folder, never by a path looked up again.
/// </summary>
public interface IScratchEntry : IDisposable
{
    ScratchEntryKind Kind { get; }

    /// <summary>The names inside the directory this entry holds.</summary>
    IEnumerable<string> Names();

    /// <summary>The named entry inside the directory this entry holds, opened without following it.</summary>
    IScratchEntry Open(string name);

    /// <summary>Removes the entry itself. A link goes as a link; what it points at is untouched.</summary>
    void Delete();
}

/// <summary>
/// Empties and removes a job's folder without ever descending through a link.
///
/// <para>A recursive delete that resolves paths as it goes can be walked out of the folder: swap a
/// subfolder for a junction between listing it and entering it, and SYSTEM deletes wherever the
/// junction points. Here every entry is judged on its own open handle, found inside the folder its
/// parent's handle holds, and a link is removed as a link without being listed.</para>
/// </summary>
public static class ScratchTree
{
    /// <summary>Far deeper than any job writes; a tree this deep was not made by a job.</summary>
    public const int MaximumDepth = 32;

    public static void Delete(IScratchEntry entry) => Delete(entry, 0);

    private static void Delete(IScratchEntry entry, int depth)
    {
        if (entry.Kind == ScratchEntryKind.Directory)
        {
            if (depth >= MaximumDepth)
            {
                throw new IOException("The job folder is nested deeper than any job writes, so it was not removed.");
            }

            foreach (var name in entry.Names().ToArray())
            {
                using var child = entry.Open(name);
                Delete(child, depth + 1);
            }
        }

        entry.Delete();
    }
}

/// <summary>
/// A job's own folder under the work folder: created fresh, and removed without following links.
/// Windows does both through handles, because there the work folder is shared with every other
/// account on the machine by default. Elsewhere the worker runs in a container or under its own
/// account, and keeps the plain calls it has always used.
/// </summary>
public static class JobScratch
{
    /// <summary>Null once the folder exists for this job alone; otherwise why the job must not start.</summary>
    public static string? Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsScratch.CreateJobFolder(path);
        }

        Directory.CreateDirectory(path);
        return null;
    }

    public static void Delete(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsScratch.DeleteTree(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
