using Optimisarr.Api.Library;

namespace Optimisarr.Api.Endpoints;

internal sealed record StartCalibrationRequest(
    int MediaFileId,
    bool HdrPlaybackConfirmed = false,
    bool DiagnosticsEnabled = false,
    bool IgnoreActiveStreams = false);
internal sealed record CalibrationClassificationsRequest(
    IReadOnlyDictionary<string, string>? Classifications);

internal static class CalibrationEndpoints
{
    public static void MapCalibrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libraries/{id:int}/calibration/sources", async (
            int id,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
            Results.Ok(await calibration.ListSourcesAsync(id, cancellationToken)))
        .WithName("ListCalibrationSources");

        app.MapPost("/api/libraries/{id:int}/calibration", async (
            int id,
            StartCalibrationRequest request,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await calibration.CreateAsync(
                    id,
                    request.MediaFileId,
                    request.HdrPlaybackConfirmed,
                    request.DiagnosticsEnabled,
                    request.IgnoreActiveStreams,
                    cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return ApiErrors.NotFound("calibration.sourceNotFound", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.BadRequest("calibration.unavailable", exception.Message);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return ApiErrors.BadRequest("calibration.unavailable", exception.Message);
            }
        })
        .WithName("StartCalibration");

        app.MapGet("/api/calibration/{id:guid}", async (
            Guid id,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            var session = await calibration.GetAsync(id, cancellationToken);
            return session is null ? Results.NotFound() : Results.Ok(session);
        })
        .WithName("GetCalibration");

        app.MapPost("/api/calibration/{id:guid}/classifications", async (
            Guid id,
            CalibrationClassificationsRequest request,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await calibration.ClassifyAsync(
                    id,
                    request.Classifications ?? new Dictionary<string, string>(),
                    cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return ApiErrors.NotFound("calibration.notFound", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.Conflict("calibration.classificationConflict", exception.Message);
            }
        })
        .WithName("ClassifyCalibrationVariants");

        app.MapPost("/api/calibration/{id:guid}/apply", async (
            Guid id,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await calibration.ApplyAsync(id, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return ApiErrors.NotFound("calibration.notFound", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.Conflict("calibration.applyConflict", exception.Message);
            }
        })
        .WithName("ApplyCalibration");

        app.MapGet("/api/calibration/{id:guid}/variants/{variant}/samples/{sampleIndex:int}/content", async (
            Guid id,
            string variant,
            int sampleIndex,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            var stream = await calibration.ResolveStreamAsync(
                id,
                variant.ToUpperInvariant(),
                sampleIndex,
                cancellationToken);
            return FileServing.ServeFile(stream?.Path);
        })
        .WithName("GetCalibrationVariantContent");

        app.MapDelete("/api/calibration/{id:guid}", async (
            Guid id,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
            await calibration.DeleteAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound())
        .WithName("DeleteCalibration");
    }
}
