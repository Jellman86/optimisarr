using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Library;

namespace Optimisarr.Api.Workers;

/// <summary>
/// The two checks every worker-facing route shares: is the feature on, and is the caller a worker.
///
/// Shared rather than repeated so a new route cannot accidentally ship without the opt-in gate —
/// the switch is only a real boundary if nothing can be added that forgets it.
/// </summary>
internal static class WorkerGate
{
    /// <summary>Non-null when the request must be refused because remote workers are switched off.</summary>
    public static async Task<IResult?> RefusedAsync(
        SettingsStore settings,
        CancellationToken cancellationToken)
    {
        var queue = await settings.GetQueueSettingsAsync(cancellationToken);
        if (queue.RemoteWorkersEnabled)
        {
            return null;
        }

        return Results.Json(
            new ApiError("workers.disabled",
                "Remote workers are turned off. Enable them in Settings to pair a sidecar."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Covers absent, malformed, unknown, and revoked credentials alike.</summary>
    public static IResult Unauthenticated() =>
        Results.Json(
            new ApiError("worker.credential.invalid", "Unknown or revoked worker credential."),
            statusCode: StatusCodes.Status401Unauthorized);
}
