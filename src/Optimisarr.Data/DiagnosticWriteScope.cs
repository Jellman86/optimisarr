using Microsoft.EntityFrameworkCore.Storage;

namespace Optimisarr.Data;

/// <summary>Owns a diagnostic write transaction only when the caller has not already opened one.</summary>
public sealed class DiagnosticWriteScope(IDbContextTransaction? transaction) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken token = default) => transaction?.CommitAsync(token) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
