namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Puts a path where FFmpeg expects a filter option value.
///
/// <para>The libvmaf log is named inside the filter description — <c>log_path=…</c> — and that is
/// not a place a Windows path can simply be dropped. Inside a filter description a backslash
/// escapes the next character and a colon ends the option, so
/// <c>C:\OptimisarrWork\job-1\vmaf.json</c> arrives at the parser as
/// <c>C</c>, then an option named <c>\OptimisarrWorkjob-1vmaf.json</c>, and FFmpeg refuses the
/// whole graph:</para>
///
/// <code>
/// No option name near 'OptimisarrWorkjob-5924sample-vmaf-q20-0.json:shortest=1:repeatlast=0'
/// Error parsing filterchain … Invalid argument
/// </code>
///
/// <para>Every quality search on this platform failed there, and the sidecar reported only that a
/// sample "could not be measured".</para>
///
/// <para>Forward slashes, which FFmpeg accepts on Windows and which cannot be mistaken for
/// escapes. Then <b>two</b> backslashes before the colon, not one: the description is unescaped
/// twice on its way in — once by the filtergraph parser and again by the filter's own option
/// parser — so a single backslash is consumed by the first and the colon still ends the option at
/// the second. Run against the real FFmpeg on the machine, of five spellings only
/// <c>C\\:/path</c> and <c>'C\:/path'</c> produce a log; <c>C\:/path</c>, <c>C:/path</c> and
/// <c>'C:/path'</c> all fail. The unquoted form is used here because there is no quote to
/// balance around a path that may contain anything else.</para>
/// </summary>
public static class FilterPath
{
    public static string ForFilterOption(string path) =>
        path
            .Replace('\\', '/')
            .Replace("'", @"\\'", StringComparison.Ordinal)
            .Replace(":", @"\\:", StringComparison.Ordinal);
}
