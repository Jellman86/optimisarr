using Optimisarr.Core.Stats;

namespace Optimisarr.Tests;

public sealed class SavingsHistoryTests
{
    [Theory]
    [InlineData("Original 1,282,683,553 bytes, output 368,922,428 bytes (-71.2% change).", 1_282_683_553, 368_922_428)]
    [InlineData("Original 1.282.683.553 bytes, output 368.922.428 bytes (-71,2% change).", 1_282_683_553, 368_922_428)]
    [InlineData("Original 1 282 683 553 bytes, output 368 922 428 bytes (-71,2% change).", 1_282_683_553, 368_922_428)]
    [InlineData("Original 487,199,181 bytes, output 675,258,979 bytes (+38.6% change). Output is not smaller than the original.", 487_199_181, 675_258_979)]
    public void Legacy_size_detail_is_read_whatever_the_digit_grouping(string detail, long original, long output)
    {
        var sizes = LegacySizeEvidence.TryParse(detail);

        Assert.NotNull(sizes);
        Assert.Equal(original, sizes.Value.OriginalBytes);
        Assert.Equal(output, sizes.Value.OutputBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Output decoded fully with no FFmpeg errors.")]
    [InlineData("Original bytes, output bytes.")]
    public void Unrelated_detail_has_no_size_evidence(string detail) =>
        Assert.Null(LegacySizeEvidence.TryParse(detail));

    [Fact]
    public void Report_json_yields_the_size_saving_check_and_harmonic_vmaf()
    {
        const string json = """
            {"checks":[
              {"name":"Decode health","outcome":"Passed","detail":"Output decoded fully with no FFmpeg errors."},
              {"name":"Size saving","outcome":"Passed","detail":"Original 1,000,000 bytes, output 250,000 bytes (-75% change)."}],
             "vmaf":{"measured":true,"error":null,"scores":{"vmafMean":93.1,"vmafHarmonicMean":92.4,"vmafMin":80}}}
            """;

        var evidence = LegacySizeEvidence.FromReportJson(json);

        Assert.Equal(1_000_000, evidence.Sizes?.OriginalBytes);
        Assert.Equal(250_000, evidence.Sizes?.OutputBytes);
        Assert.Equal(92.4, evidence.VmafHarmonicMean);
    }

    [Fact]
    public void Missing_or_broken_report_json_yields_nothing()
    {
        Assert.Null(LegacySizeEvidence.FromReportJson(null).Sizes);
        Assert.Null(LegacySizeEvidence.FromReportJson("{not json").Sizes);
        Assert.Null(LegacySizeEvidence.FromReportJson("{\"checks\":[]}").VmafHarmonicMean);
    }

    [Fact]
    public void Daily_savings_cover_every_day_in_the_window_oldest_first()
    {
        var now = new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);
        var days = DailySavings.Bucket([], now, days: 7, TimeSpan.Zero);

        Assert.Equal(7, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 19), days[0].Date);
        Assert.Equal(new DateOnly(2026, 9, 25), days[^1].Date);
        Assert.All(days, day => Assert.Equal(0, day.BytesSaved));
    }

    [Fact]
    public void Daily_savings_sum_by_the_viewer_local_day_and_ignore_the_rest()
    {
        var now = new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);
        var bst = TimeSpan.FromHours(1);
        var entries = new[]
        {
            // 23:30 UTC on the 24th is already the 25th in the UK.
            new SavingsEntry(new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.Zero), 1_000, 400),
            new SavingsEntry(new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero), 2_000, 500),
            new SavingsEntry(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero), 900, 300),
            // Outside the window, and a candidate that grew: neither counts as space saved.
            new SavingsEntry(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), 5_000, 1_000),
            new SavingsEntry(new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero), 1_000, 1_200),
        };

        var days = DailySavings.Bucket(entries, now, days: 3, bst);

        Assert.Equal([new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25)], days.Select(d => d.Date));
        Assert.Equal(0, days[0].BytesSaved);
        Assert.Equal(600, days[1].BytesSaved);
        Assert.Equal(1, days[1].Files);
        Assert.Equal(600 + 1_500, days[2].BytesSaved);
        Assert.Equal(2, days[2].Files);
    }
}
