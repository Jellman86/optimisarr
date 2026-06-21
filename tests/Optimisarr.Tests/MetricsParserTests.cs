using Optimisarr.Core.Metrics;

namespace Optimisarr.Tests;

public sealed class MetricsParserTests
{
    [Fact]
    public void Cpu_sample_sums_total_and_counts_idle_plus_iowait()
    {
        // user nice system idle iowait irq softirq steal
        var sample = CpuSample.Parse("cpu  100 0 50 800 40 0 10 0");

        Assert.NotNull(sample);
        Assert.Equal(1000, sample!.Value.Total);
        Assert.Equal(840, sample.Value.Idle); // 800 idle + 40 iowait
    }

    [Theory]
    [InlineData("")]
    [InlineData("intr 12345 0 0")]
    [InlineData("cpu0 100 0 50 800")] // per-core line, not the aggregate
    [InlineData("cpu 100 0")] // too few columns
    public void Cpu_sample_rejects_lines_that_are_not_the_aggregate_cpu_line(string line)
    {
        Assert.Null(CpuSample.Parse(line));
    }

    [Fact]
    public void Cpu_utilisation_is_busy_time_over_total_time_between_two_samples()
    {
        var previous = new CpuSample(Total: 1000, Idle: 800);
        var current = new CpuSample(Total: 1100, Idle: 850); // +100 total, +50 idle -> 50 busy

        Assert.Equal(50.0, CpuSample.Utilisation(previous, current), precision: 3);
    }

    [Fact]
    public void Cpu_utilisation_is_zero_when_no_time_elapsed()
    {
        var sample = new CpuSample(1000, 800);
        Assert.Equal(0, CpuSample.Utilisation(sample, sample));
    }

    [Fact]
    public void Drm_fdinfo_parses_client_id_driver_and_engine_nanoseconds()
    {
        var fdinfo = string.Join('\n',
            "pos:\t0",
            "flags:\t02000002",
            "drm-driver:\ti915",
            "drm-pdev:\t0000:00:02.0",
            "drm-client-id:\t42",
            "drm-engine-render:\t123456789 ns",
            "drm-engine-copy:\t0 ns",
            "drm-engine-video:\t98765432 ns");

        var client = DrmFdinfoParser.ParseClient(fdinfo);

        Assert.NotNull(client);
        Assert.Equal(42, client!.ClientId);
        Assert.Equal("i915", client.Driver);
        Assert.Equal(123456789, client.EngineNanos["render"]);
        Assert.Equal(98765432, client.EngineNanos["video"]);
        // A zero-busy engine is omitted.
        Assert.False(client.EngineNanos.ContainsKey("copy"));
    }

    [Theory]
    [InlineData("pos:\t0\nflags:\t02\nino:\t99")] // not a DRM fd
    [InlineData("drm-driver:\ti915\ndrm-client-id:\t7")] // DRM fd but no engine counters
    [InlineData("")]
    public void Drm_fdinfo_returns_null_for_non_gpu_or_idle_handles(string fdinfo)
    {
        Assert.Null(DrmFdinfoParser.ParseClient(fdinfo));
    }

    [Fact]
    public void Drm_engine_utilisation_headlines_the_busiest_engine()
    {
        var previous = new Dictionary<string, long> { ["render"] = 0, ["video"] = 0 };
        // Over 1 second (1e9 ns): render busy 700ms (70%), video busy 300ms (30%).
        var current = new Dictionary<string, long> { ["render"] = 700_000_000, ["video"] = 300_000_000 };

        var (percent, engine) = DrmEngineUtilisation.Busiest(previous, current, elapsedNanos: 1_000_000_000);

        Assert.Equal("render", engine);
        Assert.Equal(70.0, percent, precision: 1);
    }

    [Fact]
    public void Drm_engine_utilisation_is_zero_when_nothing_advanced()
    {
        var snapshot = new Dictionary<string, long> { ["video"] = 500 };
        var (percent, engine) = DrmEngineUtilisation.Busiest(snapshot, snapshot, elapsedNanos: 1_000_000_000);

        Assert.Equal(0, percent);
        Assert.Null(engine);
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("0\n", 0)]
    [InlineData("100 %", 100)]
    [InlineData("", null)]
    [InlineData("N/A", null)]
    public void Nvidia_smi_utilisation_parses_the_first_gpu(string output, int? expected)
    {
        Assert.Equal(expected, GpuValueParsers.ParseNvidiaSmiUtilisation(output));
    }

    [Theory]
    [InlineData("37\n", 37)]
    [InlineData("0", 0)]
    [InlineData("not-a-number", null)]
    public void Sysfs_busy_percent_parses_the_integer(string content, int? expected)
    {
        Assert.Equal(expected, GpuValueParsers.ParseSysfsBusyPercent(content));
    }
}
