using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Substituting this machine's paths into the server's arguments. The worker changes paths and
/// nothing else: the encode, its flags and the container were all chosen by the server, and a
/// worker that edited them would be delivering something other than what was asked for.
/// </summary>
public sealed class AssignmentPlaceholderTests
{
    [Fact]
    public void The_output_placeholder_is_a_prefix_and_keeps_the_extension_the_server_chose()
    {
        // The extension decides subtitle codecs and the muxer, so it is part of the encode contract
        // and must never be this machine's guess. Replacing the whole token would throw it away.
        var resolved = AssignmentPlaceholders.Resolve(
            ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
            @"C:\OptimisarrWork\source.mkv",
            @"C:\OptimisarrWork\candidate");

        Assert.Equal(
            ["-i", @"C:\OptimisarrWork\source.mkv", "-c:v", "hevc_nvenc", @"C:\OptimisarrWork\candidate.mkv"],
            resolved);
    }

    [Fact]
    public void A_placeholder_appearing_more_than_once_is_substituted_everywhere()
    {
        var resolved = AssignmentPlaceholders.Resolve(
            ["-i", "{{input}}", "-i", "{{input}}", "{{output}}.mp4"],
            "src.mkv", "out");

        Assert.Equal(["-i", "src.mkv", "-i", "src.mkv", "out.mp4"], resolved);
    }

    [Fact]
    public void Arguments_carrying_no_placeholder_are_passed_through_untouched()
    {
        // Including anything that merely looks like a path. The worker is not interpreting the
        // command, only filling in the two things only it can know.
        string[] given = ["-map", "0:v:0", "-b:v", "5M", "-metadata", "title=The {{best}} Film"];

        Assert.Equal(given, AssignmentPlaceholders.Resolve(given, "src.mkv", "out"));
    }

    [Fact]
    public void Windows_paths_with_spaces_survive_substitution_as_single_arguments()
    {
        // Argv, never a command line: a path with spaces is one argument and stays one. Building a
        // string here is how a file called "The Dinosaurs S01E01.mkv" becomes three arguments.
        var resolved = AssignmentPlaceholders.Resolve(
            ["-i", "{{input}}"], @"C:\Optimisarr Work\The Dinosaurs S01E01.mkv", "out");

        Assert.Equal(2, resolved.Count);
        Assert.Equal(@"C:\Optimisarr Work\The Dinosaurs S01E01.mkv", resolved[1]);
    }
}
