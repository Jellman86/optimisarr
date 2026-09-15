using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Reading a claim off the wire, with the client's own options, against the payload the server
/// actually sends.
///
/// <para>This is the check that was missing. The macOS sidecar dropped a perfectly well-formed
/// quality search for a week because its decoder wanted three fields the server was not sending,
/// and nothing anywhere decoded a real payload. This platform reads only the measurement's
/// commands, so it escaped — but it escaped by luck, and luck is not a test.</para>
/// </summary>
public sealed class AssignmentDecodingTests
{
    /// The claim response as the server sends it, search and all.
    private const string ClaimJson = """
        {
          "leaseId": "6f1f0b0e-4a1e-4f6f-9f31-2a4a5a6b7c8d",
          "jobId": 5911,
          "title": "Curious George - S14E05 - Monkey Hill",
          "sourceBytes": 520582476,
          "videoEncoder": "hevc_nvenc",
          "vmaf": "Cpu",
          "expiresUtc": "2026-09-15T09:00:00+00:00",
          "renewWithinSeconds": 30,
          "arguments": ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
          "outputExtension": "mkv",
          "quality": {
            "measure": true,
            "model": "vmaf_v0.6.1",
            "frameSubsample": 1,
            "clipVmaf": false,
            "minimumHarmonicMean": 85,
            "minimumMinimum": 70,
            "commands": [["-i", "{{distorted}}", "-i", "{{reference}}", "-lavfi", "libvmaf"]],
            "sampling": "Three 40-second samples (early, middle and late)."
          },
          "search": {
            "quality": 24,
            "sampleCommands": [["-i", "{{input}}", "-crf", "24", "{{output}}.mkv"]],
            "measurement": {
              "measure": true,
              "model": "vmaf_v0.6.1",
              "frameSubsample": 1,
              "clipVmaf": false,
              "minimumHarmonicMean": 85,
              "minimumMinimum": 70,
              "commands": [["-i", "{{distorted}}", "-i", "{{reference}}", "-lavfi", "libvmaf"]],
              "sampling": "Adaptive sample at quality 24"
            }
          }
        }
        """;

    [Fact]
    public void A_claim_carrying_a_search_arrives_with_one_to_run()
    {
        var assignment = JsonSerializer.Deserialize<Assignment>(ClaimJson, SidecarClient.Json);

        Assert.NotNull(assignment);
        Assert.Equal(5911, assignment.JobId);
        Assert.NotNull(assignment.Search);
        Assert.Equal(24, assignment.Search.Quality);
        // One sample encode and one libvmaf command for it: the runner refuses a step where those
        // two counts disagree, so a shape that loses either would fail the job rather than search.
        Assert.Single(assignment.Search.SampleCommands);
        Assert.Single(assignment.Search.Measurement.Commands);
        Assert.Equal("vmaf_v0.6.1", assignment.Search.Measurement.Model);
    }

    [Fact]
    public void The_search_measurement_is_read_as_a_thing_to_measure()
    {
        // The three fields the planner's own contract does not carry. When the server sent that
        // contract instead, these silently became false, 0 and false here — harmless only because
        // nothing on this platform reads them yet.
        var assignment = JsonSerializer.Deserialize<Assignment>(ClaimJson, SidecarClient.Json);

        Assert.True(assignment!.Search!.Measurement.Measure);
        Assert.Equal(1, assignment.Search.Measurement.FrameSubsample);
        Assert.False(assignment.Search.Measurement.ClipVmaf);
    }

    [Fact]
    public void A_claim_with_no_search_is_an_ordinary_claim()
    {
        // Every job whose quality is already settled arrives this way, so an absent search must
        // stay an ordinary thing rather than becoming an error. Written out rather than cut from
        // the payload above: the first attempt searched that string for a newline, which passed on
        // macOS and threw on the Windows runner — a test measuring its own line endings.
        const string withoutSearch = """
            {
              "leaseId": "6f1f0b0e-4a1e-4f6f-9f31-2a4a5a6b7c8d",
              "jobId": 5911,
              "title": "Curious George - S14E05 - Monkey Hill",
              "sourceBytes": 520582476,
              "videoEncoder": "hevc_nvenc",
              "vmaf": "Cpu",
              "expiresUtc": "2026-09-15T09:00:00+00:00",
              "renewWithinSeconds": 30,
              "arguments": ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
              "outputExtension": "mkv",
              "quality": {
                "measure": false,
                "model": "vmaf_v0.6.1",
                "frameSubsample": 1,
                "clipVmaf": false,
                "minimumHarmonicMean": 0,
                "minimumMinimum": 0,
                "commands": [],
                "sampling": "None"
              }
            }
            """;

        var assignment = JsonSerializer.Deserialize<Assignment>(withoutSearch, SidecarClient.Json);

        Assert.NotNull(assignment);
        Assert.Null(assignment.Search);
        Assert.Equal("hevc_nvenc", assignment.VideoEncoder);
    }

    [Fact]
    public void A_direction_that_ends_the_search_carries_the_command_to_encode_with()
    {
        // The other half of the conversation. Without these arguments the runner fails the job
        // outright rather than encoding at the baseline, which is the failure the search exists to
        // avoid — so this pins the field name the server answers with.
        const string json = """
            {
              "nextStep": null,
              "selectedQuality": 22,
              "reason": "Quality 22 met the target.",
              "arguments": ["-i", "{{input}}", "-crf", "22", "{{output}}.mkv"]
            }
            """;

        var direction = JsonSerializer.Deserialize<AdaptiveSearchDirection>(json, SidecarClient.Json);

        Assert.NotNull(direction);
        Assert.Null(direction.NextStep);
        Assert.Equal(22, direction.SelectedQuality);
        Assert.NotNull(direction.Arguments);
        Assert.Contains("22", direction.Arguments);
    }

    [Fact]
    public void A_direction_that_continues_the_search_carries_the_next_candidate()
    {
        const string json = """
            {
              "nextStep": {
                "quality": 26,
                "sampleCommands": [["-i", "{{input}}", "-crf", "26", "{{output}}.mkv"]],
                "measurement": {
                  "measure": true,
                  "model": "vmaf_v0.6.1",
                  "frameSubsample": 1,
                  "clipVmaf": false,
                  "minimumHarmonicMean": 85,
                  "minimumMinimum": 70,
                  "commands": [["-i", "{{distorted}}", "-i", "{{reference}}"]],
                  "sampling": "Adaptive sample at quality 26"
                }
              },
              "selectedQuality": null,
              "reason": "Quality 24 cleared the target; trying 26."
            }
            """;

        var direction = JsonSerializer.Deserialize<AdaptiveSearchDirection>(json, SidecarClient.Json);

        Assert.NotNull(direction);
        Assert.NotNull(direction.NextStep);
        Assert.Equal(26, direction.NextStep.Quality);
        Assert.Single(direction.NextStep.Measurement.Commands);
    }
}
