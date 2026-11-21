namespace Optimisarr.Tests;

/// <summary>
/// The Python interpreter the process-bound tests script against. These tests fake a media tool
/// as a Python script and execute it, so the interpreter's location decides whether they can run
/// — and hard-coding <c>/usr/bin/python3</c> made them fail on a machine that simply does not
/// ship it there. Tests own their fixtures and never depend on machine-specific paths, so the
/// interpreter is resolved once: <c>OPTIMISARR_TEST_PYTHON3</c> when set, otherwise a
/// <c>python3</c> found on PATH, otherwise the conventional location.
/// </summary>
public static class TestPython
{
    public static readonly string Interpreter = Resolve();

    public static string Shebang => "#!" + Interpreter + "\n";

    private static string Resolve()
    {
        if (Environment.GetEnvironmentVariable("OPTIMISARR_TEST_PYTHON3") is { Length: > 0 } configured
            && File.Exists(configured))
        {
            return configured;
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim();
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = OperatingSystem.IsWindows()
                ? Path.Combine(directory, "python3.exe")
                : Path.Combine(directory, "python3");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "/usr/bin/python3";
    }
}
