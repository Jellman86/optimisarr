using Optimisarr.Core.Diagnostics;

namespace Optimisarr.Tests;

public sealed class DiagnosticCapturePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(168)]
    public void Only_disclosed_capture_durations_are_accepted(int hours)
    {
        Assert.True(DiagnosticCapturePolicy.IsAllowedDuration(hours));
        Assert.False(DiagnosticCapturePolicy.IsAllowedDuration(2));
    }

    [Fact]
    public void Manual_session_remains_active_after_restart_until_stopped()
    {
        var started = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.True(DiagnosticCapturePolicy.AllowsEvent(started, null, null, null,
            jobId: 42, nowUtc: started.AddDays(1)));
        Assert.False(DiagnosticCapturePolicy.AllowsEvent(started, null, started.AddHours(1), null,
            jobId: 42, nowUtc: started.AddDays(1)));
    }

    [Fact]
    public void Expiry_and_job_scope_apply_before_any_enhanced_event_is_written()
    {
        var started = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var expires = started.AddHours(1);

        Assert.True(DiagnosticCapturePolicy.AllowsEvent(started, expires, null, 42,
            jobId: 42, nowUtc: started.AddMinutes(59)));
        Assert.False(DiagnosticCapturePolicy.AllowsEvent(started, expires, null, 42,
            jobId: 43, nowUtc: started.AddMinutes(59)));
        Assert.False(DiagnosticCapturePolicy.AllowsEvent(started, expires, null, 42,
            jobId: 42, nowUtc: expires));
    }
}
