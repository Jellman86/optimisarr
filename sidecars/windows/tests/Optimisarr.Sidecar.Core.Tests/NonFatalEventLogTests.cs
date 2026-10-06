using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NonFatalEventLogTests
{
    [Fact]
    public void An_access_denied_write_does_not_stop_the_worker_and_has_a_safe_notice()
    {
        var notices = new List<EventLogWriteState>();
        using var provider = new NonFatalEventLogProvider(new Sink(() =>
            throw new InvalidOperationException("secret should not be retained", new Win32Exception(5))),
            notices.Add, TimeProvider.System);
        var logger = provider.CreateLogger("worker");
        logger.LogInformation("Starting");
        Assert.Equal(new EventLogWriteState(false, 5), Assert.Single(notices));
    }

    [Fact]
    public void A_failed_sink_is_throttled_and_recovers_after_thirty_seconds()
    {
        var clock = new Clock();
        var attempts = 0;
        var notices = new List<EventLogWriteState>();
        using var provider = new NonFatalEventLogProvider(new Sink(() =>
        {
            if (++attempts == 1) throw new Win32Exception(5);
        }), notices.Add, clock);
        var first = provider.CreateLogger("host");
        var second = provider.CreateLogger("worker");
        for (var i = 0; i < 100; i++) first.LogInformation("Starting");
        clock.Advance(29);
        second.LogInformation("Working");
        Assert.Equal(1, attempts);
        Assert.Single(notices);
        clock.Advance(1);
        second.LogInformation("Still working");
        first.LogInformation("Finished");
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { new EventLogWriteState(false, 5), new EventLogWriteState(true, null) }, notices);
    }

    [Fact]
    public void Repeated_sink_failures_have_one_notice_until_a_successful_write()
    {
        var clock = new Clock();
        var notices = new List<EventLogWriteState>();
        using var provider = new NonFatalEventLogProvider(new Sink(() => throw new IOException()), notices.Add, clock);
        var logger = provider.CreateLogger("worker");
        for (var i = 0; i < 100; i++)
        {
            logger.LogError("Work status");
            clock.Advance(30);
        }
        Assert.Equal(new EventLogWriteState(false, null), Assert.Single(notices));
    }

    [Fact]
    public async Task Native_sink_writes_from_different_categories_are_serialized()
    {
        var running = 0;
        var overlapped = false;
        var writes = 0;
        using var provider = new NonFatalEventLogProvider(new Sink(() =>
        {
            if (Interlocked.Increment(ref running) != 1) overlapped = true;
            Thread.SpinWait(10000);
            Interlocked.Increment(ref writes);
            Interlocked.Decrement(ref running);
        }), _ => Assert.Fail("No fault expected"), TimeProvider.System);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            provider.CreateLogger($"category{index}").LogInformation("Concurrent startup"))));
        Assert.Equal(100, writes);
        Assert.False(overlapped);
    }

    [Fact]
    public void Application_errors_and_formatting_errors_are_not_swallowed()
    {
        using var broken = new NonFatalEventLogProvider(new Sink(() => throw new InvalidOperationException("Application bug")),
            _ => Assert.Fail("Not a sink failure"), TimeProvider.System);
        Assert.Throws<InvalidOperationException>(() => broken.CreateLogger("worker").LogInformation("Starting"));
        var writes = 0;
        using var provider = new NonFatalEventLogProvider(new Sink(() => writes++),
            _ => Assert.Fail("Not a sink failure"), TimeProvider.System);
        Assert.Throws<IOException>(() => provider.CreateLogger("worker").Log(LogLevel.Error,
            new EventId(1), "state", null, (_, _) => throw new IOException("Formatter bug")));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void An_unwritable_notice_file_does_not_stop_the_worker()
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "This is a file, so no child directory can be created.");
        try
        {
            var notice = new EventLogAvailabilityFile(Path.Combine(root, "child", "availability.log"), TimeProvider.System);
            using var provider = new NonFatalEventLogProvider(new Sink(() => throw new Win32Exception(5)), notice.Write, TimeProvider.System);
            provider.CreateLogger("worker").LogInformation("Starting");
        }
        finally { File.Delete(root); }
    }

    [Fact]
    public void Availability_notices_are_bounded_and_contain_only_safe_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-log-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "availability.log");
        try
        {
            var notice = new EventLogAvailabilityFile(path, TimeProvider.System);
            for (var i = 0; i < 1000; i++) notice.Write(new(i % 2 == 0, 5));
            Assert.InRange(new FileInfo(path).Length, 1, EventLogAvailabilityFile.MaximumBytes + 256);
            Assert.InRange(new FileInfo(path + ".previous").Length, 1, EventLogAvailabilityFile.MaximumBytes + 256);
            var text = File.ReadAllText(path);
            Assert.Contains("Event Log writes unavailable (native code 5)", text);
            Assert.Contains("Event Log writes recovered", text);
            Assert.DoesNotContain(root, text);
            Assert.DoesNotContain("secret", text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [WindowsLoggingFact, SupportedOSPlatform("windows")]
    public void The_host_replaces_every_raw_event_log_provider()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = "OptimisarrSidecar");
        Program.ConfigureEventLogging(builder.Logging);
        using var services = builder.Services.BuildServiceProvider();
        var providers = services.GetServices<ILoggerProvider>().ToArray();
        Assert.DoesNotContain(providers, provider => provider is EventLogLoggerProvider);
        Assert.Single(providers.OfType<NonFatalEventLogProvider>());
    }

    [WindowsAdminLoggingFact, SupportedOSPlatform("windows")]
    public void A_fresh_native_source_accepts_concurrent_startup_entries()
    {
        var source = "OptimisarrLogProbe_" + Guid.NewGuid().ToString("N");
        Assert.False(EventLog.SourceExists(source));
        try
        {
            EventLog.CreateEventSource(source, "Application");
            var notices = new List<EventLogWriteState>();
            using var provider = new NonFatalEventLogProvider(new NativeEventLogProvider(new EventLogSettings
            { SourceName = source, LogName = "Application" }), notices.Add, TimeProvider.System);
            var registration = provider.CreateLogger("registration");
            // Windows refreshes its registered sources asynchronously. Prove a real
            // write before concurrent entry assertions; an initial refusal must recover
            // through the production 30-second retry, not be treated as instant readiness.
            var started = Stopwatch.StartNew();
            var registered = false;
            while (started.Elapsed < TimeSpan.FromSeconds(95))
            {
                registration.LogInformation("Synthetic source registration probe; no job or pairing.");
                if (ReadOwnedEntries(0, started, TimeSpan.FromSeconds(95)).Length > 0)
                { registered = true; break; }
                Thread.Sleep(1000);
            }
            Assert.True(registered, "Native Event Log registration did not become writable within three retry windows.");
            if (notices.Count > 0)
            {
                Assert.False(notices[0].Available);
                Assert.True(notices[^1].Available);
            }
            var noticesBefore = notices.Count;
            Parallel.For(0, 100, index => provider.CreateLogger($"category{index}")
                .LogInformation(new EventId(1000 + index), "Synthetic logging probe {Index}; no job or pairing.", index));
            provider.CreateLogger("empty").LogInformation(new EventId(1100), "");
            Assert.Equal(noticesBefore, notices.Count);
            var written = Array.Empty<int>();
            var publication = Stopwatch.StartNew();
            while (publication.Elapsed < TimeSpan.FromSeconds(30))
            {
                written = ReadOwnedEntries(1000, publication, TimeSpan.FromSeconds(30));
                if (written.Length >= 101) break;
                Thread.Sleep(100);
            }
            Assert.Equal(Enumerable.Range(1000, 101), written.Order());

            int[] ReadOwnedEntries(int minimumId, Stopwatch phase, TimeSpan budget)
            {
                // Query only this source's selected IDs. Registration events cannot
                // substitute for a dropped concurrent write or the empty-message write.
                while (true)
                {
                    try
                    {
                        var query = new EventLogQuery("Application", PathType.LogName,
                            $"*[System[Provider[@Name='{source}'] and EventID >= {minimumId}]]");
                        using var reader = new EventLogReader(query);
                        var ids = new List<int>();
                        while (true)
                        {
                            var remaining = budget - phase.Elapsed;
                            if (remaining <= TimeSpan.Zero)
                                throw new TimeoutException("The owned Event Log query exceeded its deadline.");
                            using var entry = reader.ReadEvent(remaining < TimeSpan.FromSeconds(1)
                                ? remaining : TimeSpan.FromSeconds(1));
                            if (phase.Elapsed >= budget)
                                throw new TimeoutException("The owned Event Log query exceeded its deadline.");
                            if (entry is null) return ids.ToArray();
                            ids.Add(entry.Id);
                        }
                    }
                    catch (UnauthorizedAccessException) when (phase.Elapsed < budget)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }
        finally { if (EventLog.SourceExists(source)) EventLog.DeleteEventSource(source); }
    }

    [WindowsLoggingFact, SupportedOSPlatform("windows")]
    public void Native_scope_and_exception_formatting_errors_propagate_before_the_write()
    {
        var notices = new List<EventLogWriteState>();
        var sink = new NativeEventLogProvider(new EventLogSettings { SourceName = "OptimisarrSidecar" });
        using var provider = new NonFatalEventLogProvider(sink, notices.Add, TimeProvider.System);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("worker");
        using (logger.BeginScope(new ThrowingScope()))
            Assert.Throws<IOException>(() => logger.LogInformation("Starting"));
        Assert.Throws<IOException>(() => logger.LogError(new ThrowingException(), "Starting"));
        Assert.Empty(notices);
    }

    private sealed class ThrowingScope
    {
        public override string ToString() => throw new IOException("Scope bug");
    }

    private sealed class ThrowingException : Exception
    {
        public override string ToString() => throw new IOException("Exception formatting bug");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Sink(Action write) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => write();
    }
}

public sealed class WindowsLoggingFactAttribute : FactAttribute
{
    public WindowsLoggingFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows Event Log or Windows service registration.";
    }
}

public sealed class WindowsAdminLoggingFactAttribute : FactAttribute
{
    public WindowsAdminLoggingFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows and an administrator token for a disposable Event Log source.";
        else if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Requires an administrator token to create a disposable Windows Event Log source; elevated CI runs this integration check.";
    }
}
