using System.ComponentModel;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Optimisarr.Sidecar.Service;

internal sealed record EventLogWriteState(bool Available, int? NativeErrorCode);

[ProviderAlias("EventLog")]
internal sealed class NonFatalEventLogProvider(
    ILoggerProvider sink, Action<EventLogWriteState> notify, TimeProvider clock)
    : ILoggerProvider, ISupportExternalScope
{
    private readonly object _gate = new();
    private DateTimeOffset? _retryAt;
    private bool _disposed;

    public ILogger CreateLogger(string categoryName)
    {
        lock (_gate) return new SafeLogger(this, sink.CreateLogger(categoryName));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sink.Dispose();
        }
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        lock (_gate)
            if (sink is ISupportExternalScope scoped) scoped.SetScopeProvider(scopeProvider);
    }

    private static bool IsSinkFailure(Exception error) => error is
        Win32Exception or IOException or UnauthorizedAccessException or SecurityException
        or InvalidOperationException { InnerException: Win32Exception };

    private void Write(Action write)
    {
        // EventLogSettings shares one native EventLog between categories. Serialize its
        // initialization and writes; a broken diagnostic sink must not stop a worker.
        lock (_gate)
        {
            if (_disposed || _retryAt > clock.GetUtcNow()) return;
            try
            {
                write();
            }
            catch (Exception error) when (IsSinkFailure(error))
            {
                var firstFailure = _retryAt is null;
                _retryAt = clock.GetUtcNow().AddSeconds(30);
                if (firstFailure)
                    Notify(new(false, (error as Win32Exception ?? error.InnerException as Win32Exception)?.NativeErrorCode));
                return;
            }
            if (_retryAt is not null)
            {
                _retryAt = null;
                Notify(new(true, null));
            }
        }
    }

    private void Notify(EventLogWriteState state)
    {
        try { notify(state); }
        catch (Exception error) when (IsSinkFailure(error))
        {
            try { Console.Error.WriteLine("Optimisarr could not record Event Log availability; the worker continues."); }
            catch (IOException) { }
        }
    }

    private sealed class SafeLogger(NonFatalEventLogProvider owner, ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // Formatting is application code. Its exceptions must never be mistaken for
            // an unavailable OS logging sink, even if the exception type happens to match.
            var message = formatter(state, exception);
            var write = inner is IPreparedEventLogLogger native
                ? native.PrepareWrite(logLevel, eventId, message, exception)
                : () => inner.Log(logLevel, eventId, state, exception, (_, _) => message);
            owner.Write(write);
        }
    }
}
