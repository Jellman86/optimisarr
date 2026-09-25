using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Persists explicit diagnostic consent and bounded, structured job transitions.</summary>
public sealed class DiagnosticCaptureStore(OptimisarrDbContext db)
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static readonly TimeSpan RoutineRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureRetention = TimeSpan.FromDays(30);

    public async Task<DiagnosticCaptureSession> StartAsync(
        int? durationHours,
        int? scopedJobId,
        bool includePaths,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticCapturePolicy.IsAllowedDuration(durationHours)
            || scopedJobId is <= 0)
        {
            throw new ArgumentException("Choose a disclosed capture duration and a valid job ID.");
        }

        await StartLock.WaitAsync(cancellationToken);
        try
        {
            if (await GetActiveAsync(nowUtc, cancellationToken) is not null)
            {
                throw new InvalidOperationException("Stop the active diagnostic capture before starting another.");
            }

            var session = new DiagnosticCaptureSession
            {
                Id = Guid.NewGuid(),
                StartedAt = nowUtc,
                ExpiresAt = durationHours is { } hours ? nowUtc.AddHours(hours) : null,
                ScopedJobId = scopedJobId,
                IncludePaths = includePaths
            };
            db.DiagnosticCaptureSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            return session;
        }
        finally
        {
            StartLock.Release();
        }
    }

    public async Task<DiagnosticCaptureSession?> GetActiveAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        (await db.DiagnosticCaptureSessions
                .AsNoTracking()
                .Where(session => session.StoppedAt == null)
                .ToListAsync(cancellationToken))
            .Where(session => DiagnosticCapturePolicy.IsRunning(
                session.StartedAt, session.ExpiresAt, session.StoppedAt, nowUtc))
            .OrderByDescending(session => session.StartedAt)
            .FirstOrDefault();

    public async Task<DiagnosticCaptureSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.DiagnosticCaptureSessions.AsNoTracking()
            .FirstOrDefaultAsync(session => session.Id == id, cancellationToken);

    public async Task<DiagnosticCaptureSession?> GetLatestAsync(CancellationToken cancellationToken) =>
        (await db.DiagnosticCaptureSessions.AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(session => session.StartedAt)
            .FirstOrDefault();

    public async Task<bool> StopAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var session = await db.DiagnosticCaptureSessions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (session is null || session.StoppedAt is not null)
        {
            return false;
        }

        session.StoppedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Prunes ended sessions and their events. Active explicit consent is never removed.</summary>
    public async Task<int> PruneEndedAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var sessions = await db.DiagnosticCaptureSessions.ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var session in sessions)
        {
            var endedAt = session.StoppedAt ?? session.ExpiresAt;
            if (endedAt is null || endedAt > nowUtc)
            {
                continue;
            }

            var hasFailure = await db.DiagnosticEvents.AsNoTracking()
                .AnyAsync(entry => entry.SessionId == session.Id
                    && (entry.ReasonCode.StartsWith("Failure.")
                        || entry.CurrentStatus == "Failed"), cancellationToken);
            var retention = hasFailure ? FailureRetention : RoutineRetention;
            if (endedAt.Value.Add(retention) > nowUtc)
            {
                continue;
            }

            db.DiagnosticCaptureSessions.Remove(session);
            removed++;
        }

        if (removed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return removed;
    }
}
