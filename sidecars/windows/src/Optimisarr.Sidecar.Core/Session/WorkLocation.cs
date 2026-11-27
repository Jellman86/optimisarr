namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Where the configured work location came from.</summary>
public enum WorkLocationSource
{
    Default,
    OverrideFile,
    Environment,
}

/// <summary>
/// The folder this machine's jobs work in, or why it has none. A null <see cref="Path"/> means a
/// configured location could not be used, and the worker must take no work rather than put it
/// somewhere nobody chose.
/// </summary>
public sealed record WorkLocationChoice(string? Path, WorkLocationSource Source, string? Problem)
{
    public bool Usable => Path is not null;
}

/// <summary>
/// Where this machine's job source, candidate and scratch files live, and how a non-default
/// location is chosen.
///
/// <para>The default sits on the system drive, which is rarely where anyone wants hundreds of
/// gigabytes of transcode scratch. An administrator can move it with a one-line override file
/// under ProgramData, which upgrades and uninstalls leave in place, or with the machine-wide
/// <c>OPTIMISARR_SIDECAR_WORK</c> variable the Linux worker also reads. The file wins.</para>
///
/// <para><b>A configured location that cannot be used stops work instead of falling back.</b>
/// Someone who moved scratch off <c>C:</c>, or onto a RAM drive, did so for a reason: quietly
/// writing to the default instead would fill the drive they were protecting, or wear the disk
/// they meant to spare, and nothing would say so. The worker stays connected, reports no free
/// working space, and logs why. Only an absent or blank setting means "not configured".</para>
///
/// <para>The rules are written out rather than left to <see cref="System.IO.Path"/>, which parses
/// Windows paths only on Windows. They have to give the same answer in the Linux test run.</para>
/// </summary>
public static class WorkLocation
{
    public const string OverrideFileName = "work-location";
    public const string EnvironmentVariable = "OPTIMISARR_SIDECAR_WORK";
    public const string DefaultPath = @"C:\OptimisarrWork";

    /// <summary>
    /// The machine-wide variable that accepts a work folder on a drive without access control.
    /// Only the value <c>1</c> counts: anything else, or nothing, keeps such a drive refused.
    /// </summary>
    public const string AllowUnprotectedVariable = "OPTIMISARR_SIDECAR_WORK_ALLOW_UNPROTECTED";

    /// <summary>
    /// Long enough for any sensible folder, and short enough that a job's own folder and files
    /// beneath it stay inside the 260 characters older Windows tools still assume.
    /// </summary>
    public const int MaximumPathLength = 200;

    /// <summary>Longer than any sensible one-line path, short enough that reading it costs nothing.</summary>
    public const int MaximumOverrideFileBytes = 4096;

    private const string Example = @"D:\OptimisarrWork";

    /// <summary>
    /// The override file first, then the environment variable, then the default. A present but
    /// unusable setting is not skipped: it is the answer, with the reason.
    /// </summary>
    /// <param name="overrideFileProblem">Why the override file exists but was not read or not
    /// trusted. It fails closed, exactly as unusable contents do.</param>
    public static WorkLocationChoice Resolve(string? fromOverrideFile, string? fromEnvironment, string? overrideFileProblem = null)
    {
        if (overrideFileProblem is not null)
        {
            return Refused(WorkLocationSource.OverrideFile, overrideFileProblem);
        }

        if (!string.IsNullOrWhiteSpace(fromOverrideFile))
        {
            return Choose(fromOverrideFile, WorkLocationSource.OverrideFile);
        }

        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? new(DefaultPath, WorkLocationSource.Default, null)
            : Choose(fromEnvironment, WorkLocationSource.Environment);
    }

    /// <summary>Whether the machine-wide opt-in for a drive without access control is set.</summary>
    public static bool AllowsUnprotected(string? value) => value?.Trim() == "1";

    /// <summary>
    /// What an ordinary user may never have done to the override file. It decides where a
    /// LocalSystem service creates and deletes folders, so it is trusted only when it is owned by,
    /// and changeable only by, SYSTEM, Administrators or TrustedInstaller. The installer gives the
    /// folder exactly that access; a hand-made folder may not.
    /// </summary>
    /// <param name="ownerSid">The file's owner, or null when it could not be read.</param>
    /// <param name="access">The file's access list. Any right beyond reading counts as a change.</param>
    public static string? OverrideFileTrustProblem(string? ownerSid, IEnumerable<AccessEntry> access)
    {
        if (!WorkAccess.IsTrusted(ownerSid))
        {
            return $"The work-location file is not owned by Administrators or SYSTEM, so it was not trusted. {FixAccess}";
        }

        return WorkAccess.UntrustedWriters(access).Count > 0
            ? $"The work-location file can be changed by accounts other than Administrators and SYSTEM, so it was not trusted. {FixAccess}"
            : null;
    }

    /// <summary>
    /// The override file must be an ordinary file with one name. A link could be pointed elsewhere
    /// after it was checked, and a second hard link in a folder an ordinary account controls would
    /// let that account rewrite the file through a name nobody looks at.
    /// </summary>
    public static string? OverrideFileShapeProblem(bool isLink, bool isDirectory, uint links)
    {
        if (isLink)
        {
            return "The work-location file is a link, so it was not trusted. Replace it with an ordinary file.";
        }

        if (isDirectory)
        {
            return "The work-location file is a folder. Replace it with a file holding one line.";
        }

        return links == 1
            ? null
            : "The work-location file has another name (a hard link) elsewhere on the drive, so it was not trusted. Delete it and create it again.";
    }

    /// <summary>
    /// The override file's bytes as one path. A byte-order mark decides the encoding, which is how
    /// Notepad and both PowerShells mark UTF-16; without one the text is read as UTF-8.
    /// </summary>
    public static (string? Path, string? Problem) ReadOverrideBytes(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ReadOverrideText(reader.ReadToEnd());
    }

    /// <summary>One line for the Event Log at start, so the location in use is never a guess.</summary>
    /// <param name="workFolderProblem">Why the chosen folder itself was refused, if it was.</param>
    public static string Describe(WorkLocationChoice choice, bool? memoryBacked, string? workFolderProblem = null)
    {
        if (choice.Path is null)
        {
            return $"No work location: {choice.Problem} This worker takes no jobs until it is corrected and the service is restarted.";
        }

        if (workFolderProblem is not null)
        {
            // Checked again before every check-in, so ordinary work resumes once it is put right.
            return $"Work folder {choice.Path} cannot be used: {workFolderProblem} This worker takes no jobs until it is corrected.";
        }

        var from = choice.Source switch
        {
            WorkLocationSource.OverrideFile => "the work-location file",
            WorkLocationSource.Environment => EnvironmentVariable,
            _ => "the default",
        };
        var storage = memoryBacked switch
        {
            true => "a volume Windows reports as a RAM disk",
            false => "a volume Windows reports as a disk (a RAM drive reported this way is still limited by its own size)",
            null => "a volume whose free space cannot be read, so no jobs are taken until it can",
        };
        return $"Working files go to {choice.Path} (from {from}), on {storage}.";
    }

    /// <summary>Null when the path is usable; otherwise what is wrong with it, in words to act on.</summary>
    public static string? PathProblem(string path)
    {
        if (path.StartsWith('"') || path.StartsWith('\''))
        {
            return $"{path} is quoted. Write the path without quotes, such as {Example}.";
        }

        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return $"\"{path}\" is a network share or device path. Use a folder on a local drive, such as {Example}.";
        }

        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':')
        {
            return $"\"{path}\" is not a full path. Give the drive and folder, such as {Example}.";
        }

        if (path.Length == 2 || !IsSeparator(path[2]))
        {
            // "D:work" is relative to whatever folder the service last used on D:.
            return $"\"{path}\" is not a full path. Give the drive and folder, such as {Example}.";
        }

        var components = Components(path);
        if (components.Length == 0)
        {
            return $"\"{path}\" is the root of a drive. Use a folder on it, such as {path[0]}:\\OptimisarrWork.";
        }

        if (path.Length > MaximumPathLength)
        {
            return $"\"{path}\" is too long: keep it to {MaximumPathLength} characters, so each job's own folder and files fit beneath it.";
        }

        if (path.AsSpan(2).IndexOfAny(Forbidden) >= 0 || path.Any(char.IsControl))
        {
            return $"\"{path}\" contains characters Windows does not allow in a folder name. Write the path without quotes.";
        }

        if (path.Contains('\uFFFD'))
        {
            return $"\"{path}\" could not be read as text. Save the work-location file as UTF-8.";
        }

        if (components.Any(component => component is "." or ".."))
        {
            return $"\"{path}\" contains \".\" or \"..\". Name the folder directly.";
        }

        if (components.FirstOrDefault(NamesDevice) is { } device)
        {
            return $"\"{path}\" contains \"{device}\", which Windows reserves for a device rather than a folder. Choose another name.";
        }

        return components.Any(component => component.EndsWith(' ') || component.EndsWith('.'))
            ? $"\"{path}\" has a folder name ending in a space or full stop, which Windows silently removes. Rename it."
            : null;
    }

    /// <summary>The override file's text as one path, or why it is not one.</summary>
    public static (string? Path, string? Problem) ReadOverrideText(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        return lines.Length switch
        {
            0 => (null, null),
            1 => (lines[0], null),
            _ => (null, "The work-location file holds more than one line. It must hold exactly one folder."),
        };
    }

    private static WorkLocationChoice Choose(string raw, WorkLocationSource source)
    {
        var trimmed = raw.Trim();
        return PathProblem(trimmed) is { } problem
            ? Refused(source, Origin(source) + problem)
            : new(Normalise(trimmed), source, null);
    }

    private static WorkLocationChoice Refused(WorkLocationSource source, string problem) => new(null, source, problem);

    private static string Origin(WorkLocationSource source) => source == WorkLocationSource.OverrideFile
        ? "The work-location file cannot be used: "
        : $"{EnvironmentVariable} cannot be used: ";

    private static string Normalise(string path) =>
        char.ToUpperInvariant(path[0]) + @":\" + string.Join('\\', Components(path));

    private static string[] Components(string path) =>
        path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// CON, NUL, COM1 and the rest open a device in every folder, with or without an extension:
    /// "D:\\work\\nul.txt" is the null device, not a file. Windows also ignores trailing spaces
    /// before the extension when it decides.
    /// </summary>
    private static bool NamesDevice(string component)
    {
        var stem = component.Split('.')[0].TrimEnd(' ');
        if (ReservedDeviceNames.Contains(stem))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3');
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
    };

    // A colon after the drive would name an alternate data stream, not a folder.
    private static readonly char[] Forbidden = ['<', '>', '"', '|', '?', '*', ':'];

    private const string FixAccess =
        "From an administrator command prompt, run: icacls \"%ProgramData%\\Optimisarr\\Sidecar\\work-location\" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F "
        + "then: icacls \"%ProgramData%\\Optimisarr\\Sidecar\\work-location\" /setowner *S-1-5-32-544 and restart the service. "
        + "If the file still carries other accounts' access, delete it and create it again from the administrator prompt.";
}
