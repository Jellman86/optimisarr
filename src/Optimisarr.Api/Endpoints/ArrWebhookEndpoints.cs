using System.Text;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Security;
using Optimisarr.Core.Activity;
using Optimisarr.Core.IO;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

/// <summary>The receiving side of a Sonarr/Radarr webhook delivery.</summary>
public sealed record ArrWebhookReceipt(string Outcome, string Detail, IReadOnlyList<ArrWebhookFileReceipt> Files);

public sealed record ArrWebhookFileReceipt(string Path, string Outcome, string Detail);

internal static class ArrWebhookEndpoints
{
    /// <summary>A season's "On Import Complete" with every episode is far below this.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>How long a delivery waits for the one before it before telling the manager to try later.</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(60);

    public static void MapArrWebhookEndpoints(this WebApplication app)
    {
        // Sonarr and Radarr let the webhook use POST or PUT; both are accepted.
        app.MapMethods(ArrWebhookAuth.PathPrefix + "/{id:int}", ["POST", "PUT"], ReceiveAsync)
            .WithName("ReceiveArrWebhook")
            .WithSummary("Receives a Sonarr/Radarr webhook delivery, authenticated by the connection's webhook password (HTTP basic).")
            .Produces<ArrWebhookReceipt>()
            .Produces<ArrWebhookReceipt>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/arr-connections/{id:int}/webhook-password", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (connection is null)
            {
                return ApiErrors.NotFound("arr.notFound", $"No arr connection with id {id}.", new { id });
            }

            // Replacing the fingerprint is what revokes the old password; there is nothing else to clear.
            var password = ArrWebhookAuth.IssuePassword();
            connection.WebhookSecretFingerprint = ArrWebhookAuth.Fingerprint(password);
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new ArrWebhookPasswordDto(ArrWebhookAuth.Username, password, ArrWebhookAuth.PathFor(id)));
        })
        .WithName("CreateArrWebhookPassword");

        app.MapDelete("/api/arr-connections/{id:int}/webhook-password", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (connection is null)
            {
                return ApiErrors.NotFound("arr.notFound", $"No arr connection with id {id}.", new { id });
            }

            connection.WebhookSecretFingerprint = null;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ArrConnectionDto.From(connection));
        })
        .WithName("DeleteArrWebhookPassword");
    }

    private static async Task<IResult> ReceiveAsync(
        int id,
        HttpRequest request,
        OptimisarrDbContext db,
        ArrWebhookGate gate,
        ArrWebhookService webhooks,
        CancellationToken cancellationToken)
    {
        var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        // An unknown connection, one without a password and a wrong password all look the same from outside.
        if (!ArrWebhookAuth.IsAuthorized(request, connection?.WebhookSecretFingerprint))
        {
            request.HttpContext.Response.Headers.WWWAuthenticate = ArrWebhookAuth.Realm;
            return Results.Json(new ApiError("arrWebhook.unauthorized", "Webhook password required."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (request.ContentLength > MaxBodyBytes
            || await BoundedStreamReader.ReadAsync(request.Body, MaxBodyBytes, cancellationToken) is not { } body)
        {
            return Results.Json(new ApiError("arrWebhook.tooLarge", "The webhook body is too large."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var delivery = ArrWebhookPayloadParser.Parse(Encoding.UTF8.GetString(body), connection!.Type);
        if (!await gate.Slot.WaitAsync(GateWait, cancellationToken))
        {
            return Results.Json(new ApiError("arrWebhook.busy", "Optimisarr is busy with earlier webhook deliveries. The next library scan finds this file."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        WebhookDeliveryResult result;
        try
        {
            result = await webhooks.HandleAsync(connection, delivery, cancellationToken);
        }
        finally
        {
            gate.Slot.Release();
        }

        var receipt = new ArrWebhookReceipt(
            result.Outcome.ToString(),
            result.Detail,
            [.. result.Files.Select(file => new ArrWebhookFileReceipt(file.Path, file.Outcome.ToString(), file.Detail))]);
        // A body the manager should never have sent is its error to see; everything else, including
        // "not in a library" and "not queued", is a successful delivery so the connection stays healthy.
        return delivery is ArrWebhookEvent.Invalid
            ? Results.Json(receipt, statusCode: StatusCodes.Status400BadRequest)
            : Results.Ok(receipt);
    }
}
