using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// A Windows path is not a path once it is inside a filter description.
/// </summary>
public sealed class FilterPathTests
{
    [Fact]
    public void A_windows_path_survives_the_filter_parser()
    {
        // What FFmpeg saw before this: backslashes eaten as escapes, the drive colon ending the
        // option, and a graph it refused outright. Every quality search on this platform died here.
        // Two backslashes, not one: the description is unescaped twice on the way in, and a single
        // backslash is eaten by the first pass. Proven against the real FFmpeg on the machine —
        // one backslash fails exactly as the raw path does.
        Assert.Equal(
            @"C\\:/OptimisarrWork/job-1/sample-vmaf-q20-0.json",
            FilterPath.ForFilterOption(@"C:\OptimisarrWork\job-1\sample-vmaf-q20-0.json"));
    }

    [Fact]
    public void Nothing_is_left_that_the_parser_reads_as_an_escape_or_a_separator()
    {
        var escaped = FilterPath.ForFilterOption(@"C:\Optimisarr Work\job-1\vmaf.json");

        Assert.DoesNotContain(@"\O", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\j", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\v", escaped, StringComparison.Ordinal);
        // The only colon left is an escaped one.
        Assert.Equal(1, escaped.Split(':').Length - 1);
        Assert.Contains(@"\\:", escaped, StringComparison.Ordinal);
        // A space needs no escaping: the whole graph is one argv element, so nothing splits on it.
        Assert.Contains("Optimisarr Work", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_posix_path_is_left_exactly_as_it_is()
    {
        // The same code runs wherever this assembly does, and a path with no colon and no
        // backslash has nothing to escape.
        const string path = "/var/lib/optimisarr/job-1/vmaf.json";
        Assert.Equal(path, FilterPath.ForFilterOption(path));
    }

    [Fact]
    public void An_apostrophe_in_a_folder_name_does_not_open_a_quoted_section()
    {
        // A quote inside a filter description starts one, and the rest of the graph would be
        // swallowed into it. Rare on a scratch path, fatal when it happens.
        Assert.Equal(
            @"C\\:/Users/Scott\\'s Work/vmaf.json",
            FilterPath.ForFilterOption(@"C:\Users\Scott's Work\vmaf.json"));
    }

    [Fact]
    public void Only_the_log_is_escaped_because_only_the_log_is_inside_the_filter()
    {
        // The inputs are ordinary arguments. Escaping those would hand FFmpeg a filename with
        // backslashes in it that no file has.
        var resolved = MeasurementPlaceholders.Resolve(
            ["-i", "{{distorted}}", "-i", "{{reference}}", "-lavfi", "[a][b]libvmaf=log_path={{log}}:shortest=1"],
            @"C:\work\candidate.mkv",
            @"C:\work\source.mkv",
            @"C:\work\vmaf.json");

        Assert.Equal(@"C:\work\candidate.mkv", resolved[1]);
        Assert.Equal(@"C:\work\source.mkv", resolved[3]);
        Assert.Equal(@"[a][b]libvmaf=log_path=C\\:/work/vmaf.json:shortest=1", resolved[5]);
    }
}
