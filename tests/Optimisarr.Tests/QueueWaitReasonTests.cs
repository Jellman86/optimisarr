using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class QueueWaitReasonTests
{
    private static TimeOnly At(int h, int m = 0) => new(h, m);

    private static QueueWaitReason.LibraryQueue Lib(string name, int count, int? startH = null, int? endH = null) =>
        new(name, count, startH is { } s ? At(s) : null, endH is { } e ? At(e) : null);

    [Fact]
    public void Null_when_nothing_is_queued()
    {
        Assert.Null(QueueWaitReason.Describe([], At(14)));
        Assert.Null(QueueWaitReason.Describe([Lib("TV", 0, 0, 5)], At(14)));
    }

    [Fact]
    public void Null_when_a_library_with_no_window_has_work()
    {
        // A library with no auto-optimise window runs anytime, so the queue is not window-blocked.
        Assert.Null(QueueWaitReason.Describe([Lib("Film", 10)], At(14)));
    }

    [Fact]
    public void Null_when_an_open_window_library_has_work()
    {
        Assert.Null(QueueWaitReason.Describe([Lib("TV", 10, 0, 5)], At(2)));
    }

    [Fact]
    public void Describes_the_single_blocked_library_and_its_window()
    {
        var reason = QueueWaitReason.Describe([Lib("TV", 1605, 0, 5)], At(14));
        Assert.Equal("1605 job(s) waiting for the TV optimise window (00:00–05:00)", reason);
    }

    [Fact]
    public void Sums_multiple_blocked_libraries_and_names_the_biggest()
    {
        var reason = QueueWaitReason.Describe([Lib("TV", 1600, 0, 5), Lib("Music", 40, 1, 6)], At(14));
        Assert.Equal("1640 job(s) waiting for their library's optimise window (e.g. TV 00:00–05:00)", reason);
    }

    [Fact]
    public void A_runnable_library_alongside_a_blocked_one_means_not_waiting()
    {
        // Film (no window) can run now even though TV is shut → queue isn't stalled.
        Assert.Null(QueueWaitReason.Describe([Lib("TV", 1600, 0, 5), Lib("Film", 5)], At(14)));
    }
}
