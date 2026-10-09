using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Persists explicit diagnostic consent and bounded, structured job transitions.</summary>
public sealed class DiagnosticCaptureStore(OptimisarrDbContext db)
{


    public async Task<DiagnosticCaptureSession> StartAsync(
        int? durationHours,
        int? scopedJobId,
        bool includePaths,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken, DiagnosticCaptureOptions? options = null)
    {
        if (!DiagnosticCapturePolicy.IsAllowedDuration(durationHours)
            || scopedJobId is <= 0)
        {
            throw new ArgumentException("Choose a disclosed capture duration and a valid job ID.");
        }

        options ??= new();
        if (options.RetentionDays is < 1 or > 30 || options.FailureRetentionDays < options.RetentionDays
            || options.FailureRetentionDays > 90 || options.MaximumBytes is < 65536 or > 16 * 1024 * 1024)
            throw new ArgumentException("Choose retention of 1–30 days, failure retention up to 90 days and storage of 64 KiB–16 MiB.");
        await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
        if (await GetActiveAsync(nowUtc, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Stop the active diagnostic capture before starting another.");
        }

        await PruneEndedAsync(nowUtc, cancellationToken);
        if (await db.DiagnosticCaptureSessions.CountAsync(cancellationToken) >= 20)
            throw new InvalidOperationException("Twenty captures are retained. Delete an ended capture before starting another.");
        var session = new DiagnosticCaptureSession
        {
            Id = Guid.NewGuid(),
            StartedAt = nowUtc,
            ExpiresAt = durationHours is { } hours ? nowUtc.AddHours(hours) : null,
            ScopedJobId = scopedJobId,
            IncludePaths = includePaths,
            PersistAcrossRestart = options.PersistAcrossRestart,
            RetentionDays = options.RetentionDays,
            FailureRetentionDays = options.FailureRetentionDays,
            MaximumBytes = options.MaximumBytes
        };
        db.DiagnosticCaptureSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken);
        return session;
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
        await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
        var session = await db.DiagnosticCaptureSessions
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (session is not null && db.Entry(session).State == EntityState.Unchanged) await db.Entry(session).ReloadAsync(cancellationToken);
        if (session is null || session.StoppedAt is not null)
        {
            return false;
        }

        session.StoppedAt = session.ExpiresAt is { } expiry && expiry < nowUtc ? expiry : nowUtc;
        await db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken);
        return true;
    }

    public async Task StopOnRestartAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var sessions = await db.DiagnosticCaptureSessions.Where(s => s.StoppedAt == null && !s.PersistAcrossRestart).ToListAsync(token);
        foreach (var session in sessions) session.StoppedAt = session.ExpiresAt is { } expiry && expiry < nowUtc ? expiry : nowUtc;
        await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
    }

    public async Task SetPinnedAsync(Guid id, bool pinned, CancellationToken token)
    {
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var session = await db.DiagnosticCaptureSessions.FirstOrDefaultAsync(s => s.Id == id, token)
            ?? throw new KeyNotFoundException("Diagnostic capture not found.");
        if (db.Entry(session).State == EntityState.Unchanged) await db.Entry(session).ReloadAsync(token);
        session.Pinned = pinned;
        await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
    }

    public async Task<bool> DeleteEndedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken token)
    {
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var session = await db.DiagnosticCaptureSessions.FirstOrDefaultAsync(s => s.Id == id, token);
        if (session is null) return false;
        if (db.Entry(session).State == EntityState.Unchanged) await db.Entry(session).ReloadAsync(token);
        if (session.Pinned || DiagnosticCapturePolicy.IsRunning(session.StartedAt, session.ExpiresAt, session.StoppedAt, nowUtc))
            throw new InvalidOperationException("Stop and unpin the capture before deleting its evidence.");
        db.DiagnosticCaptureSessions.Remove(session); await db.SaveChangesAsync(token);
        await write.CommitAsync(token); return true;
    }

    /// <summary>Prunes ended sessions and their events. Active explicit consent is never removed.</summary>
    public async Task<int> PruneEndedAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
        var sessions = await db.DiagnosticCaptureSessions.ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var session in sessions)
        {
            if (db.Entry(session).State == EntityState.Unchanged) await db.Entry(session).ReloadAsync(cancellationToken);
            var endedAt = session.StoppedAt ?? session.ExpiresAt;
            if (session.Pinned || endedAt is null || endedAt > nowUtc)
            {
                continue;
            }

            var hasFailure = session.HasFailure || await db.DiagnosticEvents.AsNoTracking()
                .AnyAsync(entry => entry.SessionId == session.Id
                    && (entry.ReasonCode.StartsWith("Failure.")
                        || entry.CurrentStatus == "Failed"), cancellationToken);
            var retention = TimeSpan.FromDays(hasFailure ? session.FailureRetentionDays : session.RetentionDays);
            if (endedAt.Value.Add(retention) > nowUtc)
            {
                continue;
            }

            db.DiagnosticCaptureSessions.Remove(session);
            removed++;
        }

        if (removed > 0) await db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken);
        return removed;
    }
}
