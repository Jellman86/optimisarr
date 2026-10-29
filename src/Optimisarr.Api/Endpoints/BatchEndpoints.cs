using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Data;
namespace Optimisarr.Api.Endpoints;

/// <summary>One item of a batch that could not be completed, with the reason it was refused.</summary>
public sealed record BatchFailure(int Id, string Code, string Message);

/// <summary>
/// The result of a batch operation: what succeeded, and what did not and why. A batch reports
/// per-item outcomes instead of failing the whole request, because one ineligible row must not
/// hide the rows that were handled.
/// </summary>
public sealed record BatchResult(int Requested, int[] Succeeded, BatchFailure[] Failures);

public sealed record BatchJobRequest(int[]? Ids, bool CancelActive = false);

public sealed record BatchRetryRequest(int[]? Ids, bool HigherQuality = false);

public sealed record BatchExcludeRequest(int[]? MediaFileIds, string? Reason = null);

public sealed record BatchUnexcludeRequest(int[]? Ids);

public sealed record BatchProbeRequest(int[]? Ids);

/// <summary>
/// The batch counterparts of the single-item Queue and Library actions. Each one applies the
/// same per-item rules as the single action it stands in for — the rules live in
/// <see cref="JobOperations"/> and <see cref="ExclusionOperations"/> — and reports what
/// happened to every selected item.
/// </summary>
internal static class BatchEndpoints
{
    /// <summary>
    /// A batch is one operator gesture, not a bulk import: the cap keeps a single request bounded
    /// and its failure report readable. The web interface holds a selection to the same number, so
    /// a request that arrives over the cap is a client that did not know the rule.
    /// </summary>
    private const int MaxBatchSize = 200;

    public static void MapBatchEndpoints(this WebApplication app)
    {
        // Remove several jobs from the queue at once. A failed or cancelled job is removed
        // outright; an active job is stopped first, but only when the operator asked for it
        // (CancelActive), which the confirmation states plainly before the request is sent.
        app.MapPost("/api/jobs/batch/remove", async (
            BatchJobRequest request,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var ids = Normalise(request.Ids);
            if (ids.Length > MaxBatchSize) return TooLarge(ids.Length);
            var result = await RunAsync(ids, db, async id =>
            {
                var outcome = await JobOperations.RemoveAsync(db, dispatcher, id, request.CancelActive, cancellationToken);
                return outcome.Succeeded ? null : new BatchFailure(id, outcome.Code, outcome.Message);
            }, loggerFactory.CreateLogger(nameof(BatchEndpoints)), cancellationToken);
            return Results.Ok(result);
        })
        .WithName("BatchRemoveJobs");

        // Re-queue several failed or cancelled jobs at once, so the operator can deliberately try
        // a set of files again after fixing whatever made them fail.
        app.MapPost("/api/jobs/batch/retry", async (
            BatchRetryRequest request,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var ids = Normalise(request.Ids);
            if (ids.Length > MaxBatchSize) return TooLarge(ids.Length);
            var result = await RunAsync(ids, db, async id =>
            {
                var outcome = await JobOperations.RetryAsync(db, dispatcher, id, request.HigherQuality, cancellationToken);
                return outcome.Succeeded ? null : new BatchFailure(id, outcome.Code, outcome.Message);
            }, loggerFactory.CreateLogger(nameof(BatchEndpoints)), cancellationToken);
            return Results.Ok(result);
        })
        .WithName("BatchRetryJobs");

        // Exclude several files from optimisation at once. An excluded file is never offered
        // again, so the operator's intent is recorded per file and reported per file.
        app.MapPost("/api/media/batch/exclude", async (
            BatchExcludeRequest request,
            OptimisarrDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var ids = Normalise(request.MediaFileIds);
            if (ids.Length > MaxBatchSize) return TooLarge(ids.Length);
            var result = await RunAsync(ids, db, async id =>
            {
                var outcome = await ExclusionOperations.ApplyAsync(db, id, request.Reason, cancellationToken);
                return outcome.Succeeded ? null : new BatchFailure(id, outcome.Code, outcome.Message);
            }, loggerFactory.CreateLogger(nameof(BatchEndpoints)), cancellationToken);
            return Results.Ok(result);
        })
        .WithName("BatchExcludeMedia");

        // Lift several exclusions at once, so a library that was excluded by mistake or has since
        // been fixed can be offered again without removing the rows one by one.
        app.MapPost("/api/exclusions/batch/remove", async (
            BatchUnexcludeRequest request,
            OptimisarrDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var ids = Normalise(request.Ids);
            if (ids.Length > MaxBatchSize) return TooLarge(ids.Length);
            var result = await RunAsync(ids, db, async id =>
            {
                var outcome = await ExclusionOperations.UndoAsync(db, id, cancellationToken);
                return outcome.Succeeded ? null : new BatchFailure(id, outcome.Code, outcome.Message);
            }, loggerFactory.CreateLogger(nameof(BatchEndpoints)), cancellationToken);
            return Results.Ok(result);
        })
        .WithName("BatchRemoveExclusions");

        // Inspect several files again with ffprobe. A probe is a process per file, so the work goes
        // to a background batch that the operator can watch, instead of a request that stays open
        // until the last file is done.
        app.MapPost("/api/media/batch/probe", (BatchProbeRequest request, MediaProbeBatches batches) =>
        {
            var ids = Normalise(request.Ids);
            if (ids.Length > MaxBatchSize) return TooLarge(ids.Length);
            var batch = batches.Start(ids);
            return batch is null
                ? Results.Json(new ApiError("batch.busy", "The probe queue is full. Wait for a batch to finish and retry."), statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(batch);
        })
        .WithName("BatchProbeMedia");

        // How a probe batch stands, and what became of every file in it.
        app.MapGet("/api/media/batch/probe/{id:int}", (int id, MediaProbeBatches batches) =>
        {
            var batch = batches.Get(id);
            return batch is null
                ? ApiErrors.NotFound("batch.notFound", $"No probe batch with id {id}.", new { id })
                : Results.Ok(batch);
        })
        .WithName("GetBatchProbeMedia");

        // Stop a running probe batch. The file it is on finishes; the rest are left alone.
        app.MapDelete("/api/media/batch/probe/{id:int}", (int id, MediaProbeBatches batches) =>
        {
            if (!batches.Cancel(id))
            {
                return ApiErrors.NotFound("batch.notFound", $"No probe batch with id {id} is running.", new { id });
            }

            return Results.NoContent();
        })
        .WithName("CancelBatchProbeMedia");
    }

    internal static async Task<BatchResult> RunAsync(int[] ids, OptimisarrDbContext db,
        Func<int, Task<BatchFailure?>> operation, ILogger logger, CancellationToken token)
    {
        var succeeded = new List<int>();
        var failures = new List<BatchFailure>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var failure = await operation(id);
                if (failure is null) succeeded.Add(id);
                else failures.Add(failure);
            }
            catch (DedupeReservationConflictException ex)
            {
                failures.Add(new(id, "dedupe.reserved", ex.Message));
            }
            catch (Exception ex) when (ex is DbUpdateException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Batch item {ItemId} could not be handled.", id);
                failures.Add(new(id, "batch.itemFailed", "Could not complete this item. Refresh its state before retrying."));
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return new(ids.Length, succeeded.ToArray(), failures.ToArray());
    }

    /// <summary>Distinct, positive ids in the order the operator selected them.</summary>
    private static int[] Normalise(int[]? ids) =>
        ids is null ? [] : ids.Where(id => id > 0).Distinct().ToArray();

    /// <summary>
    /// A batch that is over the cap is refused outright: handling part of it would leave the
    /// operator believing the whole selection had been acted on.
    /// </summary>
    private static IResult TooLarge(int requested) =>
        ApiErrors.BadRequest("batch.tooLarge", $"A batch carries at most {MaxBatchSize} rows.", new { max = MaxBatchSize, requested });
}
