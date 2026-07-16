using Optimisarr.Api.Library;

namespace Optimisarr.Api.Endpoints;

internal sealed record StartCalibrationRequest(int MediaFileId);
internal sealed record CalibrationAnswerRequest(Guid TrialId, string? Choice);

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
                return Results.Ok(await calibration.CreateAsync(id, request.MediaFileId, cancellationToken));
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

        app.MapPost("/api/calibration/{id:guid}/answers", async (
            Guid id,
            CalibrationAnswerRequest request,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await calibration.AnswerAsync(
                    id,
                    request.TrialId,
                    request.Choice?.Trim().ToUpperInvariant() ?? string.Empty,
                    cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return ApiErrors.NotFound("calibration.notFound", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.Conflict("calibration.trialConflict", exception.Message);
            }
        })
        .WithName("AnswerCalibrationTrial");

        app.MapPost("/api/calibration/{id:guid}/reveal", async (
            Guid id,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await calibration.RevealAsync(id, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return ApiErrors.NotFound("calibration.notFound", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.Conflict("calibration.notReady", exception.Message);
            }
        })
        .WithName("RevealCalibration");

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

        app.MapGet("/api/calibration/{id:guid}/trials/{trialId:guid}/content/{slot}", async (
            Guid id,
            Guid trialId,
            string slot,
            BlindCalibrationService calibration,
            CancellationToken cancellationToken) =>
        {
            var stream = await calibration.ResolveStreamAsync(
                id,
                trialId,
                slot.ToUpperInvariant(),
                cancellationToken);
            return FileServing.ServeFile(stream?.Path);
        })
        .WithName("GetCalibrationTrialContent");

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
