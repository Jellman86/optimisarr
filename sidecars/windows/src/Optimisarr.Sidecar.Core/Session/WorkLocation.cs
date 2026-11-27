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

    /// <summary>
    /// What an ordinary user may never have done to the override file. It decides where a
    /// LocalSystem service creates and deletes folders, so it is trusted only when it is owned by,
    /// and writable only by, SYSTEM, Administrators or TrustedInstaller. The installer gives the
    /// folder exactly that access; a hand-made folder may not.
    /// </summary>
    /// <param name="ownerSid">The file's owner, or null when it could not be read.</param>
    /// <param name="writers">Every principal an allow entry lets change the file or its access.</param>
    public static string? OverrideFileTrustProblem(string? ownerSid, IEnumerable<string> writers)
    {
        if (ownerSid is null || !TrustedPrincipals.Contains(ownerSid))
        {
            return $"The work-location file is not owned by Administrators or SYSTEM, so it was not trusted. {FixAccess}";
        }

        return writers.Any(writer => !TrustedPrincipals.Contains(writer))
            ? $"The work-location file can be changed by accounts other than Administrators and SYSTEM, so it was not trusted. {FixAccess}"
            : null;
    }

    /// <summary>One line for the Event Log at start, so the location in use is never a guess.</summary>
    public static string Describe(WorkLocationChoice choice, bool? memoryBacked)
    {
        if (choice.Path is null)
        {
            return $"No work location: {choice.Problem} This worker takes no jobs until it is corrected and the service is restarted.";
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

    // A colon after the drive would name an alternate data stream, not a folder.
    private static readonly char[] Forbidden = ['<', '>', '"', '|', '?', '*', ':'];

    private const string FixAccess =
        "From an administrator command prompt, run: icacls \"%ProgramData%\\Optimisarr\\Sidecar\\work-location\" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F "
        + "then: icacls \"%ProgramData%\\Optimisarr\\Sidecar\\work-location\" /setowner *S-1-5-32-544 and restart the service.";

    private static readonly HashSet<string> TrustedPrincipals = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };
}
