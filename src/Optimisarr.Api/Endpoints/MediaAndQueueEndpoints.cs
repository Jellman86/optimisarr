using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Library;
using Optimisarr.Api.Metrics;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Security;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class MediaAndQueueEndpoints
{
    public static void MapMediaAndQueueEndpoints(this WebApplication app)
    {
        // The inventory list. Optional `libraryId`, `status` (a MediaFileStatus), and `search` (a
        // case-insensitive substring of the relative path) narrow the set; `page`/`pageSize` page it. The
        // body stays a MediaFileDto array (existing callers are unaffected); the pre-paging total is returned
        // in the X-Total-Count header. Filtering, counting, ordering, and paging all run in the database.
        app.MapGet("/api/media", async (
            int? libraryId,
            string? status,
            string? search,
            int? page,
            int? pageSize,
            HttpResponse response,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            MediaFileStatus? wantedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<MediaFileStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest(
                        $"Unknown media status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<MediaFileStatus>())}.");
                }
                wantedStatus = parsed;
            }

            var result = await MediaQueries.QueryAsync(db, new MediaQuery
            {
                LibraryId = libraryId,
                Status = wantedStatus,
                Search = search,
                Page = page ?? 1,
                PageSize = pageSize ?? 0
            }, cancellationToken);

            response.Headers["X-Total-Count"] = result.Total.ToString();
            return Results.Ok(result.Items);
        })
        .WithName("ListMedia");

        app.MapPost("/api/media/{id:int}/probe", async (
            int id,
            LibraryInventoryService inventory,
            CancellationToken cancellationToken) =>
        {
            var file = await inventory.ProbeAsync(id, cancellationToken);
            if (file is null)
            {
                return Results.NotFound(new { error = $"No media file with id {id}." });
            }

            return Results.Ok(new MediaFileDto(
                file.Id,
                file.LibraryId,
                file.RelativePath,
                file.SizeBytes,
                file.Status.ToString(),
                file.MediaKind.ToString(),
                file.Container,
                file.VideoCodec,
                file.Width,
                file.Height,
                file.DurationSeconds,
                file.AudioCodecs,
                file.AudioTrackCount,
                file.SubtitleTrackCount,
                file.ProbedAt,
                file.ProbeError));
        })
        .WithName("ProbeMedia");

        // Phase 2: optimisation candidates derived from the probed inventory, with a
        // human-readable reason for every file (eligible or skipped). No FFmpeg runs here.
        app.MapGet("/api/candidates", async (
            int? libraryId,
            CandidateService candidates,
            CancellationToken cancellationToken) =>
        {
            var results = await candidates.EvaluateAsync(libraryId, cancellationToken);
            return Results.Ok(results);
        })
        .WithName("ListCandidates");

        // Per-library eligible/skipped tallies for the Libraries workspace, so the list can show each
        // library's candidate counts without fetching every probed file row.
        app.MapGet("/api/candidates/summary", async (
            CandidateService candidates,
            CancellationToken cancellationToken) =>
        {
            var summary = await candidates.SummariseAsync(cancellationToken);
            return Results.Ok(summary);
        })
        .WithName("CandidateSummary");

        // The Inventory view: discovered files paired with their rule verdict, filtered, counted, and paged
        // on the server. `show` is all/eligible/skipped/unprobed; `search` matches a path substring. The
        // response carries the page, the filtered total, and the per-filter tallies for the UI chips.
        app.MapGet("/api/inventory", async (
            int? libraryId,
            string? show,
            string? search,
            int? page,
            int? pageSize,
            InventoryQueries inventory,
            CancellationToken cancellationToken) =>
        {
            var filter = InventoryFilter.All;
            if (!string.IsNullOrWhiteSpace(show) && !Enum.TryParse(show, ignoreCase: true, out filter))
            {
                return Results.BadRequest(
                    $"Unknown inventory filter '{show}'. Valid values: {string.Join(", ", Enum.GetNames<InventoryFilter>())}.");
            }

            return Results.Ok(await inventory.QueryAsync(
                libraryId, filter, search, page ?? 1, pageSize ?? 0, cancellationToken));
        })
        .WithName("GetInventory");

        // Phase 3: enqueue a library's eligible candidates as transcode jobs.
        app.MapPost("/api/libraries/{id:int}/enqueue", async (
            int id,
            OptimisarrDbContext db,
            JobEnqueueService enqueue,
            QueueDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
            if (library is null)
            {
                return Results.NotFound(new { error = $"No library with id {id}." });
            }

            var result = await enqueue.EnqueueEligibleAsync(library, cancellationToken);
            if (result.Enqueued > 0)
            {
                dispatcher.Wake();
            }
            return Results.Ok(result);
        })
        .WithName("EnqueueLibrary");

        // Phase 11: settings preview. Queue a throwaway transcode of one file with its library's
        // resolved settings so the operator can compare original vs encoded before committing.
        app.MapPost("/api/media/{id:int}/preview", async (
            int id,
            PreviewService preview,
            CancellationToken cancellationToken) =>
        {
            var jobId = await preview.CreateAsync(id, cancellationToken);
            return jobId is null
                ? Results.NotFound(new { error = $"No media file with id {id} (or it has no library)." })
                : Results.Ok(new { jobId });
        })
        .WithName("CreatePreview");

        app.MapGet("/api/preview/{jobId:int}", async (
            int jobId,
            PreviewService preview,
            CancellationToken cancellationToken) =>
        {
            var comparison = await preview.GetAsync(jobId, cancellationToken);
            return comparison is null ? Results.NotFound() : Results.Ok(comparison);
        })
        .WithName("GetPreview");

        app.MapDelete("/api/preview/{jobId:int}", async (
            int jobId,
            PreviewService preview,
            CancellationToken cancellationToken) =>
        {
            var deleted = await preview.DeleteAsync(jobId, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeletePreview");

        // Streams the original media file for side-by-side comparison (range-enabled so the browser
        // can seek video/audio). Paths come from the database, never from the request.
        app.MapGet("/api/media/{id:int}/content", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var path = await db.MediaFiles
                .Where(file => file.Id == id)
                .Select(file => file.Path)
                .FirstOrDefaultAsync(cancellationToken);
            return FileServing.ServeFile(path);
        })
        .WithName("GetMediaContent");

        // Streams a preview job's encoded output for the comparison view.
        app.MapGet("/api/preview/{jobId:int}/content", async (
            int jobId,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var path = await db.Jobs
                .Where(job => job.Id == jobId && job.Type == JobType.Preview)
                .Select(job => job.WorkOutputPath)
                .FirstOrDefaultAsync(cancellationToken);
            return FileServing.ServeFile(path);
        })
        .WithName("GetPreviewContent");

        // The queue feed and the diagnostics list. All query params are optional: status, libraryId, and
        // category (a FailureCategory) narrow the set; since/until (ISO-8601) bound a job's finished/enqueued
        // time; page/pageSize page the result. The body stays a JobDto array (so existing callers are
        // unaffected); the pre-paging total is returned in the X-Total-Count header.
        app.MapGet("/api/jobs", async (
            string? status,
            int? libraryId,
            string? category,
            DateTimeOffset? since,
            DateTimeOffset? until,
            int? page,
            int? pageSize,
            HttpResponse response,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            JobStatus? wantedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest(
                        $"Unknown job status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<JobStatus>())}.");
                }
                wantedStatus = parsed;
            }

            FailureCategory? wantedCategory = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                if (!Enum.TryParse<FailureCategory>(category, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest(
                        $"Unknown failure category '{category}'. Valid values: {string.Join(", ", Enum.GetNames<FailureCategory>())}.");
                }
                wantedCategory = parsed;
            }

            var result = await JobQueries.QueryAsync(db, new JobQuery
            {
                Status = wantedStatus,
                LibraryId = libraryId,
                Category = wantedCategory,
                Since = since,
                Until = until,
                Page = page ?? 1,
                PageSize = pageSize ?? 0
            }, cancellationToken);

            response.Headers["X-Total-Count"] = result.Total.ToString();
            return Results.Ok(result.Items);
        })
        .WithName("ListJobs");

        // Diagnostics: failed jobs grouped by classified reason (size-saving, container incompatibility,
        // replacement collision, …) with counts and recent samples, so "why are jobs failing?" is answerable
        // from the API without reading container logs. Optionally scoped to one library.
        app.MapGet("/api/jobs/failures", async (int? libraryId, OptimisarrDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await JobQueries.SummariseFailuresAsync(db, cancellationToken, libraryId)))
        .WithName("JobFailureSummary");

        // The captured ffmpeg log (non-progress stderr) for a failed job, as plain text, so the full reason
        // is available from the API without container access. 404 when the job is unknown or has no log
        // (only failed transcodes capture one).
        app.MapGet("/api/jobs/{id:int}/log", async (int id, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var log = await db.Jobs
                .AsNoTracking()
                .Where(job => job.Id == id)
                .Select(job => job.ProcessLog)
                .FirstOrDefaultAsync(cancellationToken);

            return string.IsNullOrEmpty(log) ? Results.NotFound() : Results.Text(log, "text/plain");
        })
        .WithName("JobLog");

        // Backdrop artwork for a job's title, proxied from the first connected media server. 404 when
        // there's no server, no match, or the file isn't film/TV — the hero just shows no background then.
        app.MapGet("/api/jobs/{id:int}/artwork", async (
            int id,
            ArtworkService artwork,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await artwork.GetBackdropAsync(id, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(result.Value.Bytes, result.Value.ContentType);
        })
        .WithName("JobArtwork");

        // Thumbnail for a media file, by kind: a poster (Radarr/Sonarr first, then a media server) for video,
        // the embedded cover art for audio, and a down-scaled still for an image. Bytes are produced/proxied
        // by the backend so no server token reaches the browser. 404 when nothing is available — the UI then
        // shows its plain placeholder.
        app.MapGet("/api/media/{id:int}/thumbnail", async (
            int id,
            ArtworkService artwork,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await artwork.GetThumbnailAsync(id, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(result.Value.Bytes, result.Value.ContentType);
        })
        .WithName("MediaThumbnail");

        // Remove finished jobs (completed, failed, cancelled) to declutter the queue. A job whose
        // original is still in quarantine is the live rollback path and is kept; clearing it would
        // destroy a recorded rollback, which the safety standard forbids. Re-optimisation of the
        // cleared files is still prevented by the embedded optimisation marker, so losing the
        // history rows is safe.
        app.MapPost("/api/jobs/clear", async (string? scope, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            // scope: "finished" = completed only; "errored" = failed/cancelled; anything else (or
            // omitted) = every terminal job. The IsClearable safety check below still protects jobs
            // that hold a live rollback regardless of scope.
            var statuses = (scope?.Trim().ToLowerInvariant()) switch
            {
                "finished" => new[] { JobStatus.Completed },
                "errored" => new[] { JobStatus.Failed, JobStatus.Cancelled },
                _ => new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled },
            };

            var liveRollbackJobIds = (await db.Replacements
                    .Where(r => r.Status == ReplacementStatus.Replaced)
                    .Select(r => r.JobId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var terminal = await db.Jobs
                .Where(j => statuses.Contains(j.Status))
                .ToListAsync(cancellationToken);

            var clearable = terminal.Where(j => JobClearing.IsClearable(j, liveRollbackJobIds)).ToList();
            var clearableIds = clearable.Select(j => j.Id).ToList();

            // The Job→Replacement FK is Restrict, so spent rollback records (rolled back or purged)
            // for these jobs must be removed before their parent job.
            var spentReplacements = await db.Replacements
                .Where(r => clearableIds.Contains(r.JobId))
                .ToListAsync(cancellationToken);
            db.Replacements.RemoveRange(spentReplacements);
            db.Jobs.RemoveRange(clearable);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { cleared = clearable.Count });
        })
        .WithName("ClearJobs");

        // Reset the pending queue (e.g. after a rules change): cancel anything in flight, then remove all
        // queued and ready-to-replace jobs and discard their /work outputs. No original is touched — see
        // QueueDispatcher.ClearPendingQueueAsync.
        app.MapPost("/api/jobs/clear-pending", async (QueueDispatcher dispatcher, CancellationToken cancellationToken) =>
        {
            var cleared = await dispatcher.ClearPendingQueueAsync(cancellationToken);
            return Results.Ok(new { cleared });
        })
        .WithName("ClearPendingJobs");

        app.MapPost("/api/jobs/{id:int}/cancel", async (
            int id,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (job is null)
            {
                return Results.NotFound(new { error = $"No job with id {id}." });
            }

            if (!JobEnqueueService.ActiveStatuses.Contains(job.Status))
            {
                return Results.BadRequest(new { error = $"Job {id} is already {job.Status} and cannot be cancelled." });
            }

            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            // Stop the running ffmpeg, if this job is in flight.
            dispatcher.RequestCancel(job.Id);

            return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
        })
        .WithName("CancelJob");

        // Removes a cancelled or failed job so an operator can reset it and enqueue it again.
        // Completed replacements are deliberately excluded: their job record protects rollback state.
        app.MapDelete("/api/jobs/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (job is null)
            {
                return Results.NotFound(new { error = $"No job with id {id}." });
            }

            if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            {
                return Results.BadRequest(new { error = "Stop an active job before removing it from the queue." });
            }

            db.Jobs.Remove(job);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("RemoveResettableJob");

        // Re-queue a failed or cancelled job so the user can deliberately try a file again
        // (e.g. after fixing an encoder setting). The history guard otherwise holds failed
        // files back, so this is the explicit way to retry one.
        app.MapPost("/api/jobs/{id:int}/retry", async (
            int id,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
            if (job is null)
            {
                return Results.NotFound(new { error = $"No job with id {id}." });
            }

            if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            {
                return Results.BadRequest(new { error = $"Only failed or cancelled jobs can be retried; job {id} is {job.Status}." });
            }

            job.Status = JobStatus.Queued;
            job.ErrorMessage = null;
            job.Progress = 0;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.WorkOutputPath = null;
            job.OutputSizeBytes = null;
            job.VerificationPassed = null;
            job.VerificationReportJson = null;
            job.VerifiedAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            dispatcher.Wake();
            return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
        })
        .WithName("RetryJob");

        // --- Exclusions: files the operator never wants optimised. Durable and path-keyed, so they
        // survive clearing the queue and re-scanning — unlike the soft "previously failed" skip. ---

        app.MapExclusionEndpoints();

        // Phase 5: safe replacement. A verified ReadyToReplace job can replace its
        // original — the original is quarantined first and the move is recorded so it can
        // always be rolled back.
        app.MapPost("/api/jobs/{id:int}/replace", async (
            int id,
            ReplacementService replacement,
            CancellationToken cancellationToken) =>
        {
            var result = await replacement.ReplaceAsync(id, cancellationToken);
            return ReplacementResults.ToHttp(result);
        })
        .WithName("ReplaceFromJob");
    }
}
