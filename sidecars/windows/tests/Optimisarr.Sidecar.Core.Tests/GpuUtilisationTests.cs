using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The arithmetic behind the GPU busy figure on Windows, and the cache that keeps a slow read off
/// every caller's thread. No GPU or counter provider is needed: the readings are passed in.
/// </summary>
public sealed class GpuUtilisationTests
{
    private const string DiscreteEncode = "luid_0x00000000_0x00018002_phys_0_eng_14_engtype_VideoEncode";
    private const string Integrated3D = "luid_0x00000000_0x0000C2F6_phys_0_eng_0_engtype_3D";

    [Fact]
    public void Gpu_engine_filter_keeps_encode_engines_and_drops_unrelated_work()
    {
        // Contributed with the GPU Engine reader. Instance names are
        // "pid_<pid>_luid_..._engtype_<Engine>"; engines no transcode can occupy stay out of the figure.
        Assert.True(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_14_engtype_Video Encode"));
        Assert.True(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_12_engtype_Video Decode"));
        Assert.True(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_0_engtype_3D"));
        Assert.True(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_2_engtype_Compute"));

        Assert.False(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_13_engtype_True Audio"));
        Assert.False(GpuEngineUtilisation.IsRelevantEngine(
            "pid_100_luid_0x00000000_0x00018002_phys_0_eng_5_engtype_Timer"));
        Assert.False(GpuEngineUtilisation.IsRelevantEngine("not-an-instance-name"));
    }

    [Theory]
    [InlineData("VideoEncode")]
    [InlineData("VideoDecode")]
    [InlineData("VideoProcessing")]
    [InlineData("Video Codec_0")]
    [InlineData("Compute_1")]
    [InlineData("High Priority Compute")]
    [InlineData("Cuda")]
    [InlineData("Copy")]
    public void Each_vendor_spelling_of_a_transcode_engine_is_recognised(string engineType) =>
        Assert.True(GpuEngineUtilisation.IsRelevantEngine($"pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_{engineType}"));

    [Theory]
    [InlineData("Security")]
    [InlineData("True Audio_0")]
    [InlineData("LegacyOverlay")]
    [InlineData("Timer")]
    public void Engines_a_transcode_never_occupies_are_left_out(string engineType) =>
        Assert.False(GpuEngineUtilisation.IsRelevantEngine($"pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_{engineType}"));

    [Fact]
    public void Two_processes_on_one_encoder_engine_add_up()
    {
        // Two simultaneous encodes on one NVENC/VCN engine: each process sees its own share, and
        // the engine is as busy as both together. Taking the larger alone would report 40%.
        var busiest = GpuEngineUtilisation.BusiestEngine([
            ($"pid_100_{DiscreteEncode}", 30),
            ($"pid_200_{DiscreteEncode}", 40),
        ]);

        Assert.Equal(0.7, busiest!.Value, 6);
    }

    [Fact]
    public void Separate_adapters_are_not_added_together()
    {
        // An Intel iGPU compositing the desktop and an AMD card encoding are two GPUs. The figure
        // is the busiest one, never a sum that implies more than one device's worth of work.
        var busiest = GpuEngineUtilisation.BusiestEngine([
            ($"pid_100_{DiscreteEncode}", 65),
            ($"pid_300_{Integrated3D}", 20),
        ]);

        Assert.Equal(0.65, busiest!.Value, 6);
    }

    [Fact]
    public void Overlapping_process_shares_never_report_more_than_fully_busy() =>
        Assert.Equal(1, GpuEngineUtilisation.BusiestEngine([
            ($"pid_100_{DiscreteEncode}", 70),
            ($"pid_200_{DiscreteEncode}", 60),
        ]));

    [Fact]
    public void Nothing_relevant_to_measure_is_unknown_not_idle()
    {
        Assert.Null(GpuEngineUtilisation.BusiestEngine([]));
        Assert.Null(GpuEngineUtilisation.BusiestEngine([
            ("pid_1_luid_0x0_0x1_phys_0_eng_5_engtype_Security", 90),
            ("garbled", 50),
            ($"pid_100_{DiscreteEncode}", double.NaN),
        ]));
    }

    [Fact]
    public void An_idle_engine_is_reported_as_idle()
    {
        // Distinct from the case above: the encoder exists and was measured doing nothing.
        Assert.Equal(0, GpuEngineUtilisation.BusiestEngine([($"pid_100_{DiscreteEncode}", 0)]));
    }

    [Fact]
    public void The_first_call_does_not_wait_for_the_read_and_the_next_one_has_it()
    {
        var clock = new ManualClock();
        Action? pending = null;
        var reading = new BackgroundGpuReading(() => 0.42, clock, start: work => pending = work);

        Assert.Null(reading.Current());
        Assert.NotNull(pending);

        pending!();
        Assert.Equal(0.42, reading.Current());
    }

    [Fact]
    public void Only_one_read_runs_at_a_time_however_often_it_is_asked()
    {
        var clock = new ManualClock();
        var started = 0;
        var reading = new BackgroundGpuReading(() => 0.5, clock, start: _ => started++);

        reading.Current();
        clock.Advance(TimeSpan.FromSeconds(30));
        reading.Current();
        reading.Current();

        Assert.Equal(1, started);
    }

    [Fact]
    public void A_fresh_figure_is_reused_until_the_refresh_interval_passes()
    {
        var clock = new ManualClock();
        var reads = 0;
        var reading = new BackgroundGpuReading(() => ++reads / 10.0, clock, start: work => work());

        Assert.Equal(0.1, reading.Current());
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0.1, reading.Current());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0.2, reading.Current());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_read_that_never_finishes_lets_the_old_figure_lapse_rather_than_linger()
    {
        var clock = new ManualClock();
        var runInline = true;
        var reading = new BackgroundGpuReading(() => 0.8, clock, start: work => { if (runInline) work(); });

        Assert.Equal(0.8, reading.Current());
        runInline = false; // the next read hangs, as nvidia-smi can
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0.8, reading.Current());
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(reading.Current());
    }

    [Fact]
    public void A_failed_or_nonsensical_read_is_unknown()
    {
        var clock = new ManualClock();
        var failing = new BackgroundGpuReading(() => throw new InvalidOperationException("provider gone"), clock, start: work => work());
        var nonsense = new BackgroundGpuReading(() => double.NaN, clock, start: work => work());

        Assert.Null(failing.Current());
        Assert.Null(nonsense.Current());
    }

    [Fact]
    public void The_sampler_reports_the_cached_gpu_figure_beside_the_cpu()
    {
        var clock = new ManualClock();
        var gpu = new BackgroundGpuReading(() => 0.3, clock, start: work => work());
        var sampler = new MachineLoadSampler(readCpu: () => null, readGpu: gpu.Current);

        Assert.Equal(0.3, sampler.Sample()!.GpuBusyFraction);
    }

    [Fact]
    public void A_caller_that_asks_only_every_30_seconds_still_gets_a_figure()
    {
        // Check-ins come every 30 s and lease renewals about every 15 s. Refreshing only when
        // asked would hand each of them a figure older than the 15 s cutoff, so always nothing.
        var clock = new ManualClock();
        var pending = new Queue<Action>();
        var reads = 0;
        using var reading = new BackgroundGpuReading(() => { reads++; return 0.6; }, clock, start: pending.Enqueue);

        Assert.Null(reading.Current());
        for (var check = 0; check < 4; check++)
        {
            for (var second = 0; second < 30; second++)
            {
                // Each background read finishes within the second it was started in.
                while (pending.TryDequeue(out var work)) work();
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            while (pending.TryDequeue(out var work)) work();

            Assert.Equal(0.6, reading.Current());
        }
        Assert.True(reads >= 4 * 30 / 5, $"expected a read every five seconds, got {reads}");
    }

    [Fact]
    public void Reading_stops_once_nobody_has_asked_for_two_minutes_and_resumes_when_asked()
    {
        var clock = new ManualClock();
        var reads = 0;
        using var reading = new BackgroundGpuReading(() => { reads++; return 0.2; }, clock, start: work => work());

        reading.Current();
        clock.Advance(TimeSpan.FromMinutes(10));
        var readsWhileIdle = reads;
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(readsWhileIdle <= 2 * 60 / 5 + 2, $"kept reading for nobody: {readsWhileIdle} reads");
        Assert.Equal(readsWhileIdle, reads);
        Assert.Equal(0, clock.ActiveTimers);

        Assert.Equal(0.2, reading.Current());
        Assert.Equal(1, clock.ActiveTimers);
    }

    [Fact]
    public void Disposing_stops_the_background_reads()
    {
        var clock = new ManualClock();
        var reads = 0;
        var reading = new BackgroundGpuReading(() => { reads++; return 0.2; }, clock, start: work => work());

        reading.Current();
        reading.Dispose();
        var readsAtDisposal = reads;
        clock.Advance(TimeSpan.FromMinutes(1));
        reading.Current();

        Assert.Equal(readsAtDisposal, reads);
        Assert.Equal(0, clock.ActiveTimers);
    }

    /// <summary>
    /// A clock whose timers fire only as <see cref="Advance"/> passes their due time, so a
    /// refresh cadence can be walked step by step without real time passing.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public int ActiveTimers => _timers.Count(timer => timer.Due is not null);

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (_timers.Where(timer => timer.Due <= target).MinBy(timer => timer.Due) is { } next)
            {
                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
                next.Fire();
            }
            _now = target;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, () => callback(state));
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(ManualClock clock, Action fire) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public TimeSpan Period { get; private set; }
            public void Fire() => fire();

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                return true;
            }

            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
