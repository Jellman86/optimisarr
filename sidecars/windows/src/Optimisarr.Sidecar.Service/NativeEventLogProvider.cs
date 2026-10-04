using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace Optimisarr.Sidecar.Service;

internal interface IPreparedEventLogLogger
{
    Action PrepareWrite(LogLevel level, EventId id, string message, Exception? exception);
}

/// <summary>
/// Uses the native write directly: the standard provider silently disables itself
/// after some security failures, so its return value cannot confirm recovery.
/// Access is serialized and failures are handled by NonFatalEventLogProvider.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeEventLogProvider(EventLogSettings settings) : ILoggerProvider, ISupportExternalScope
{
    private readonly EventLogSettings _settings = settings;
    private IExternalScopeProvider? _scopes;
    private readonly EventLog _log = new(settings.LogName ?? "Application",
        settings.MachineName ?? ".", settings.SourceName ?? "OptimisarrSidecar");

    public ILogger CreateLogger(string categoryName) => new NativeLogger(this, categoryName);
    public void Dispose() => _log.Dispose();
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    private sealed class NativeLogger(NativeEventLogProvider owner, string category) : ILogger, IPreparedEventLogLogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
        public bool IsEnabled(LogLevel level) => level != LogLevel.None
            && (owner._settings.Filter?.Invoke(category, level) ?? true);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => PrepareWrite(level, id, formatter(state, exception), exception)();

        public Action PrepareWrite(LogLevel level, EventId id, string formattedMessage, Exception? exception)
        {
            // The guard already checked filtering and prepared the message. Every
            // invocation here attempts a real write, even for an empty message.
            var message = $"Category: {category}\nEventId: {id.Id}\n\n{formattedMessage}";
            owner._scopes?.ForEachScope((scope, _) => message += $"\nScope: {scope}", 0);
            if (exception is not null) message += $"\n\nException: {exception}";
            var type = level switch
            {
                LogLevel.Warning => EventLogEntryType.Warning,
                LogLevel.Error or LogLevel.Critical => EventLogEntryType.Error,
                _ => EventLogEntryType.Information
            };
            // Keep native writes bounded. Large operational messages are explicitly
            // marked as truncated rather than causing the Windows sink to reject them.
            const int limit = 16_000;
            if (message.Length > limit) message = message[..limit] + "\n[truncated]";
            var nativeEvent = new EventInstance(Math.Clamp(id.Id, 0, ushort.MaxValue), 0, type);
            return () => owner._log.WriteEvent(nativeEvent, message);
        }
    }
}
