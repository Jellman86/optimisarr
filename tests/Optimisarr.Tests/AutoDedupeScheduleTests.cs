using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class AutoDedupeScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_library_never_cleaned_is_due() => Assert.True(AutoDedupeSchedule.IsDue(null, Now));

    [Fact]
    public void A_library_cleaned_within_the_day_is_not_due() => Assert.False(AutoDedupeSchedule.IsDue(Now.AddHours(-23), Now));

    [Fact]
    public void A_library_cleaned_a_day_ago_is_due() => Assert.True(AutoDedupeSchedule.IsDue(Now.AddDays(-1), Now));

    [Fact]
    public void A_last_run_in_the_future_does_not_stop_the_schedule() => Assert.True(AutoDedupeSchedule.IsDue(Now.AddDays(3), Now));

    [Theory]
    [InlineData(MediaType.Film, true)]
    [InlineData(MediaType.Tv, true)]
    [InlineData(MediaType.Music, false)]
    [InlineData(MediaType.Photo, false)]
    [InlineData(MediaType.Other, false)]
    public void Only_film_and_tv_libraries_can_be_cleaned_automatically(MediaType type, bool supported) =>
        Assert.Equal(supported, AutoDedupeSchedule.Supports(type));

    [Fact]
    public void The_last_scan_scope_is_kept_so_saved_keeper_choices_still_apply() =>
        Assert.Equal([1, 4], AutoDedupeSchedule.Scope(4, [4, 1], new HashSet<int> { 1, 4, 9 }));

    [Fact]
    public void A_scope_whose_library_has_gone_falls_back_to_this_library() =>
        Assert.Equal([4], AutoDedupeSchedule.Scope(4, [4, 2], new HashSet<int> { 4 }));

    [Fact]
    public void With_no_earlier_scan_only_this_library_is_scanned() =>
        Assert.Equal([4], AutoDedupeSchedule.Scope(4, [], new HashSet<int> { 4 }));
}
