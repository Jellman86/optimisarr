using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// Deciding before any encoding whether a source's own picture ends so far short of its audio that
/// verification is certain to reject every encode of it. The numbers are real sources from #241.
/// </summary>
public sealed class SourceTimelinePreflightTests
{
    private const string Streams = """
        "streams": [
          { "index": 0, "codec_type": "video", "start_time": "0.000000", "disposition": { "attached_pic": 0 } },
          { "index": 1, "codec_type": "audio", "start_time": "0.031000", "disposition": { "attached_pic": 0 } },
          { "index": 2, "codec_type": "audio", "start_time": "0.000000", "disposition": { "attached_pic": 0 } },
          { "index": 3, "codec_type": "video", "start_time": "0.000000", "disposition": { "attached_pic": 1 } }
        ]
        """;

    [Fact]
    public void The_tail_read_reports_where_the_picture_and_primary_audio_end()
    {
        // Read from 30 s before the end, the picture's packets still arrive from its last keyframe
        // — which on this source is almost three minutes before the audio stops.
        var json = $$"""
            {
              "packets": [
                { "stream_index": 0, "pts_time": "2900.731000", "duration_time": "0.042000" },
                { "stream_index": 1, "pts_time": "3070.217000", "duration_time": "0.021000" },
                { "stream_index": 2, "pts_time": "3500.000000", "duration_time": "0.021000" },
                { "stream_index": 3, "pts_time": "4000.000000" }
              ],
              {{Streams}}
            }
            """;

        var tail = SourceTailTimelineParser.Parse(json)!;

        Assert.Equal(2900.773, tail.VideoEndSeconds!.Value, 3);
        // a:0 only, as the verifier measures it; the second audio track and cover art are ignored.
        Assert.Equal(3070.238, tail.AudioEndSeconds!.Value, 3);
        Assert.Equal(0.031, tail.AudioStartSeconds);
        Assert.True(SourceTimelineJudge.LooksShort(tail));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void An_unreadable_tail_is_no_evidence(string json)
    {
        Assert.Null(SourceTailTimelineParser.Parse(json));
    }

    [Fact]
    public void A_picture_that_runs_to_the_end_of_the_audio_is_clear()
    {
        var tail = new SourceTailTimeline(0.105, 2711.814, 0, 2711.637);

        Assert.False(SourceTimelineJudge.LooksShort(tail));
    }

    [Theory]
    [InlineData(2900.814, 3070.25)]   // S02E02, 5.52% short
    [InlineData(2898.395, 3268.863)]  // S02E06, 11.33% short
    [InlineData(2362.943, 2881.365)]  // The Shield S06E02, 17.99% short
    public void A_confirmed_shortfall_is_held_with_the_verifiers_own_gate_named(double video, double audio)
    {
        var verdict = SourceTimelineJudge.Confirm(video, 0, audio, 0, videoMetadataSeconds: video);

        Assert.True(verdict.Short);
        Assert.Contains("Source video timeline", verdict.Reason);
        Assert.Contains("Verification failed", verdict.Reason);
        Assert.Contains("The original is unchanged", verdict.Reason);
    }

    [Fact]
    public void A_scan_of_only_the_opening_frames_is_never_asserted_as_a_short_source()
    {
        // Pokémon S13E34: the packet scan once ended at 0.08 s while the stream's own duration
        // agreed with 1277 s of audio. The source was fine; the scan was not. Verification, which
        // also has the encoded output, decides that case — this check stays out of it.
        var verdict = SourceTimelineJudge.Confirm(0.08, 0, 1277.27, 0, videoMetadataSeconds: 1279.2);

        Assert.False(verdict.Short);
    }

    [Theory]
    [InlineData(2711.0, 2711.6)]      // aligned
    [InlineData(100.0, 101.5)]        // over a second, but under 2%
    [InlineData(40.0, 40.9)]          // over 2%, but under a second
    public void Anything_the_gate_would_pass_is_clear(double video, double audio)
    {
        Assert.False(SourceTimelineJudge.Confirm(video, 0, audio, 0, null).Short);
    }

    [Fact]
    public void Missing_measurements_never_hold_a_source()
    {
        Assert.False(SourceTimelineJudge.Confirm(null, 0, 3070, 0, null).Short);
        Assert.False(SourceTimelineJudge.Confirm(2900, 0, null, 0, null).Short);
    }
}
