using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The CPU arithmetic, without a machine to be busy. The distinction that matters throughout is
/// between "idle" and "cannot say": the first invites someone to send this machine more work.
/// </summary>
public sealed class MachineLoadTests
{
    [Fact]
    public void Windows_folds_idle_into_the_kernel_total_and_it_has_to_come_back_out()
    {
        // 100 ticks passed, 90 of them idle. Counting kernel+user without subtracting idle would
        // report this nearly-idle machine as fully busy.
        var before = new CpuTicks(Idle: 0, Kernel: 0, User: 0);
        var after = new CpuTicks(Idle: 90, Kernel: 90, User: 10);

        Assert.Equal(0.1, CpuLoadCalculator.BusyFraction(before, after));
    }

    [Fact]
    public void Busy_time_is_measured_against_the_interval_not_all_time_since_boot()
    {
        // A machine long since booted, busy for three of the last four ticks. Reporting against
        // cumulative totals instead would leave it looking idle for ever.
        var before = new CpuTicks(Idle: 8_500, Kernel: 9_000, User: 1_000);
        var after = new CpuTicks(Idle: 8_501, Kernel: 9_001, User: 1_003);

        Assert.Equal(0.75, CpuLoadCalculator.BusyFraction(before, after));
    }

    [Fact]
    public void Cannot_say_rather_than_idle_when_there_is_nothing_to_compare()
    {
        var reading = new CpuTicks(Idle: 10, Kernel: 10, User: 0);

        // No time passed at all.
        Assert.Null(CpuLoadCalculator.BusyFraction(reading, reading));
        // Counters went backwards: the machine slept, or the values wrapped.
        Assert.Null(CpuLoadCalculator.BusyFraction(reading, new CpuTicks(Idle: 4, Kernel: 4, User: 0)));
    }

    [Fact]
    public void The_first_sample_has_nothing_to_compare_against_and_says_so()
    {
        var readings = new Queue<CpuTicks>([
            new CpuTicks(Idle: 90, Kernel: 90, User: 10),
            new CpuTicks(Idle: 170, Kernel: 180, User: 20),
        ]);
        var sampler = new MachineLoadSampler(
            readCpu: () => readings.Count > 0 ? readings.Dequeue() : null,
            readGpu: () => null);

        Assert.Null(sampler.Sample());                       // one reading is not a measurement
        Assert.Equal(0.2, sampler.Sample()!.CpuBusyFraction); // 20 busy of the 100 ticks that passed
    }

    [Fact]
    public void A_machine_that_can_read_neither_figure_reports_nothing_at_all()
    {
        // Rather than a body full of absent fields. The server then keeps whatever it last knew.
        var sampler = new MachineLoadSampler(readCpu: () => null, readGpu: () => null);
        Assert.Null(sampler.Sample());
    }

    [Fact]
    public void A_machine_with_no_accelerator_still_reports_its_cpu()
    {
        var readings = new Queue<CpuTicks>([
            new CpuTicks(0, 0, 0),
            new CpuTicks(Idle: 50, Kernel: 50, User: 50),
        ]);
        var sampler = new MachineLoadSampler(
            readCpu: () => readings.Count > 0 ? readings.Dequeue() : null,
            readGpu: () => null);

        sampler.Sample();
        var load = sampler.Sample();

        Assert.Equal(0.5, load!.CpuBusyFraction);
        Assert.Null(load.GpuBusyFraction);
    }
}
