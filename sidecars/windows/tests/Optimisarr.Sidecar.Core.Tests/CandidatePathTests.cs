using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The file the command writes and the file this machine then looks for have to be the same file.
///
/// <para>They were not. The server sends the extension without a leading dot — `mkv`, not `.mkv` —
/// and puts the dot in the command itself, as `{{output}}.mkv`. Concatenating the two rebuilt the
/// name as `candidatemkv`. FFmpeg wrote `candidate.mkv`, exited cleanly, and the sidecar reported
/// that it "produced no candidate file" and handed the job back; PICARD did that to every job it
/// was offered.</para>
/// </summary>
public sealed class CandidatePathTests
{
    [Theory]
    [InlineData("mkv")]
    [InlineData(".mkv")]
    public void The_rebuilt_name_matches_what_the_command_was_told_to_write(string extension)
    {
        // Both spellings, because the assignment carries one and the commands inside it carry the
        // other, and this machine sees both.
        const string prefix = @"C:\OptimisarrWork\job-12\candidate";
        var resolved = AssignmentPlaceholders.Resolve(
            ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
            @"C:\OptimisarrWork\job-12\source",
            prefix);

        Assert.Equal(resolved[^1], CandidatePath.For(prefix, extension));
    }

    [Fact]
    public void An_extension_the_server_spelled_with_a_dot_does_not_gain_a_second_one()
    {
        Assert.Equal(@"C:\work\candidate.mp4", CandidatePath.For(@"C:\work\candidate", ".mp4"));
    }

    [Fact]
    public void A_sample_is_named_the_same_way_as_a_finished_candidate()
    {
        // The quality search writes a dozen of these per job, and the same mistake there fails the
        // search rather than the delivery — which reads as "a sample encoded to nothing" and sends
        // the job back before a single frame of the real encode is attempted.
        var resolved = AssignmentPlaceholders.Resolve(
            ["-i", "{{input}}", "-t", "40", "{{output}}.mkv"],
            "source", @"C:\work\sample-q20-0");

        Assert.Equal(resolved[^1], CandidatePath.For(@"C:\work\sample-q20-0", "mkv"));
    }

    [Fact]
    public void No_extension_at_all_leaves_the_prefix_alone()
    {
        // Nothing sends this today, but appending a bare dot would name a file nothing can open.
        Assert.Equal(@"C:\work\candidate", CandidatePath.For(@"C:\work\candidate", ""));
        Assert.Equal(@"C:\work\candidate", CandidatePath.For(@"C:\work\candidate", "."));
    }
}
