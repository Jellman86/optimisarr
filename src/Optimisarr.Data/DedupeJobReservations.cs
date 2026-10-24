using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Workers;

namespace Optimisarr.Data;

/// <summary>The host supplies live physical identities; isolated data consumers still protect IDs and canonical paths.</summary>
public interface IFileReservationKeys
{
    IReadOnlyList<string> Resolve(int mediaFileId, params string[] paths);
}

public sealed class DedupeReservationConflictException() : InvalidOperationException(
    "This file is reserved by an unresolved duplicate action. Resolve or cancel that action before starting work.");

internal static class DedupeJobReservations
{
    public static bool IsActive(JobStatus status) => status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled);

    public static Job[] PendingJobs(OptimisarrDbContext db)
    {
        var jobs = db.ChangeTracker.Entries<Job>().Where(e => IsActive(e.Entity.Status)
            && (e.State == EntityState.Added || e.State == EntityState.Modified
                && (e.Property(j => j.Status).IsModified || e.Property(j => j.MediaFileId).IsModified
                    || e.Property(j => j.WorkOutputPath).IsModified))).Select(e => e.Entity).ToList();
        var leaseJobIds = db.ChangeTracker.Entries<JobLease>().Where(e => e.Entity.State == LeaseState.Held
            && (e.State == EntityState.Added || e.State == EntityState.Modified
                && (e.Property(l => l.State).IsModified || e.Property(l => l.JobId).IsModified)))
            .Select(e => e.Entity.JobId).ToHashSet();
        jobs.AddRange(db.ChangeTracker.Entries<Job>().Where(e => leaseJobIds.Contains(e.Entity.Id)).Select(e => e.Entity));
        return jobs.Distinct().ToArray();
    }

    public static bool HasPendingLease(OptimisarrDbContext db) => db.ChangeTracker.Entries<JobLease>().Any(e =>
        e.Entity.State == LeaseState.Held && (e.State == EntityState.Added || e.State == EntityState.Modified
            && (e.Property(l => l.State).IsModified || e.Property(l => l.JobId).IsModified)));

    public static async Task ValidateAsync(OptimisarrDbContext db, Job[] jobs, CancellationToken token)
    {
        var reserved = await db.DedupeFileClaims.AsNoTracking().Select(c => c.Key).ToHashSetAsync(token);
        if (reserved.Count == 0) return;
        var leaseIds = db.ChangeTracker.Entries<JobLease>().Where(e => e.Entity.State == LeaseState.Held
            && (e.State == EntityState.Added || e.State == EntityState.Modified
                && (e.Property(l => l.State).IsModified || e.Property(l => l.JobId).IsModified)))
            .Select(e => e.Entity.JobId).Except(jobs.Select(j => j.Id)).ToArray();
        var leasedJobs = await db.Jobs.AsNoTracking().Where(j => leaseIds.Contains(j.Id)).ToListAsync(token);
        var persistedIds = jobs.Where(j => j.Id > 0).Select(j => j.Id).ToArray();
        var persistedJobs = await db.Jobs.AsNoTracking().Where(j => persistedIds.Contains(j.Id)).ToListAsync(token);
        var references = jobs.Concat(leasedJobs).Concat(persistedJobs).DistinctBy(j => (j.MediaFileId, j.WorkOutputPath)).ToArray();
        var mediaIds = references.Select(j => j.MediaFileId).ToArray();
        // Batch inventory reads so a pending reservation does not introduce one query per enqueued job.
        var paths = await db.MediaFiles.AsNoTracking().Where(f => mediaIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Path, token);
        // New entities can share the CLR default ID before EF writes their generated keys.
        // Include all pending names for that ID conservatively, including changed inventory paths.
        var pendingPaths = db.ChangeTracker.Entries<MediaFile>().Where(e => e.State == EntityState.Added
                || e.State == EntityState.Modified && e.Property(f => f.Path).IsModified)
            .ToLookup(e => e.Entity.Id, e => e.Entity.Path);
        foreach (var job in references)
        {
            // Read current persisted paths rather than trusting a navigation loaded before a reservation.
            var keys = db.ResolveFileReservationKeys(job.MediaFileId,
                new[] { paths.GetValueOrDefault(job.MediaFileId), job.WorkOutputPath }
                    .OfType<string>().Concat(pendingPaths[job.MediaFileId]).ToArray());
            if (keys.Any(reserved.Contains))
                throw new DedupeReservationConflictException();
        }
    }

    public static IReadOnlyList<string> LexicalKeys(int id, IEnumerable<string> paths) =>
        paths.Select(p => FileClaimKeys.Path(System.IO.Path.GetFullPath(p), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
            .Prepend(FileClaimKeys.Media(id)).Distinct(StringComparer.Ordinal).ToArray();
}
