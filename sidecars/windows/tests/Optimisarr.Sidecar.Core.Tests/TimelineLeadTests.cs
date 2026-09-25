using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The number the server left a token for. It has to mean the same thing on every worker, and be
/// written the same way, or two machines are reporting measurements of different things.
/// </summary>
public sealed class TimelineLeadTests
{
    private const string TypicalProbe = """
        {
          "streams": [
            { "codec_type": "audio", "start_time": "0.000000" },
            { "codec_type": "video", "start_time": "0.042000" }
          ],
          "format": { "start_time": "0.000000" }
        }
        """;

    [Fact]
    public void The_lead_is_the_pictures_start_less_the_containers()
    {
        Assert.Equal(0.042, TimelineLead.Parse(TypicalProbe)!.Value, 6);
    }

    [Fact]
    public void A_container_that_starts_before_its_video_gives_a_positive_lead()
    {
        // Audio priming: the container begins at the audio, the picture arrives later, and two
        // files primed differently present the same picture at different instants.
        const string json = """
            {
              "streams": [{ "codec_type": "video", "start_time": "1.024000" }],
              "format": { "start_time": "1.000000" }
            }
            """;

        Assert.Equal(0.024, TimelineLead.Parse(json)!.Value, 6);
    }

    [Theory]
    [InlineData("""{ "streams": [{ "codec_type": "video", "start_time": "0.0" }] }""")]
    [InlineData("""{ "format": { "start_time": "0.0" }, "streams": [] }""")]
    [InlineData("""{ "format": { "start_time": "0.0" }, "streams": [{ "codec_type": "audio", "start_time": "0.0" }] }""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Anything_it_cannot_read_is_nothing_rather_than_a_guess(string json)
    {
        // A guessed shift would misalign the comparison it exists to align, and a window scoring
        // near zero reads as a bad encode rather than as a bad measurement. Saying nothing makes
        // the caller fall back to letting the server measure.
        Assert.Null(TimelineLead.Parse(json));
    }

    [Fact]
    public void A_number_rather_than_a_string_is_read_too()
    {
        // ffprobe writes these as strings, but its JSON has changed shape before now.
        const string json = """
            { "streams": [{ "codec_type": "video", "start_time": 0.5 }], "format": { "start_time": 0.25 } }
            """;

        Assert.Equal(0.25, TimelineLead.Parse(json)!.Value, 6);
    }

    [Fact]
    public void A_shift_is_written_the_way_the_server_writes_seconds()
    {
        Assert.Equal("0.042", TimelineLead.Shift(candidate: 0.042, source: 0));
        Assert.Equal("-0.042", TimelineLead.Shift(candidate: 0, source: 0.042));
        Assert.Equal("0.5", TimelineLead.Shift(candidate: 1.5, source: 1));
    }

    [Fact]
    public void A_gap_smaller_than_a_microsecond_is_no_gap()
    {
        // The filter graph works in microseconds. Writing 0.0000001 would send the server a long
        // decimal describing a difference it cannot express anyway.
        Assert.Equal("0", TimelineLead.Shift(candidate: 0.0000001, source: 0));
        Assert.Equal("0", TimelineLead.Shift(candidate: 1.0, source: 1.0));
        // And never "-0", which is a number no filter should have to parse.
        Assert.Equal("0", TimelineLead.Shift(candidate: 0.9999999, source: 1.0));
    }

    [Fact]
    public void The_formatting_matches_the_other_sidecar_exactly()
    {
        // Both report to the same server, which parses what comes back. A worker writing
        // "0.042000" where another writes "0.042" is a difference nobody would notice until a
        // measurement disagreed.
        Assert.DoesNotContain(".", TimelineLead.Shift(candidate: 2, source: 2), StringComparison.Ordinal);
        Assert.Equal("0.1", TimelineLead.Shift(candidate: 0.100000, source: 0));
    }

    [Fact]
    public void The_probe_asks_for_both_starts_and_nothing_else()
    {
        // The command is the contract with ffprobe: losing either start silently turns every
        // measurement into an unaligned one.
        Assert.Contains("format=start_time:stream=codec_type,start_time", TimelineLead.ProbeArguments);
        Assert.Contains("json", TimelineLead.ProbeArguments);
    }
}

/// <summary>
/// The same table of shifts is asserted in the macOS sidecar's own suite
/// (`TimelineLeadTests.theSharedTable`). Both workers report to one server, which parses what
/// comes back, so the two implementations agreeing is a contract rather than a coincidence — and
/// the only way to hold two languages to it is to write the answers down once and check them in
/// both places.
/// </summary>
public sealed class TimelineLeadSharedTableTests
{
    public static TheoryData<double, double, string> Cases() => new()
    {
        { 0.042, 0, "0.042" },
        { 0, 0.042, "-0.042" },
        { 1.5, 1, "0.5" },
        { 0.0000001, 0, "0" },
        { 1.0, 1.0, "0" },
        { 0.9999999, 1.0, "0" },
        { 2, 2, "0" },
        { 0.1, 0, "0.1" },
        { 0.0416667, 0, "0.041667" },
        { -0.5, 0.25, "-0.75" },
        { 0.0000004, 0, "0" },
        { 0.0000006, 0, "0.000001" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Both_sidecars_write_the_same_shift(double candidate, double source, string expected) =>
        Assert.Equal(expected, TimelineLead.Shift(candidate, source));
}
