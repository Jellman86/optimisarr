using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
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

internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var queue = await settings.GetQueueSettingsAsync(cancellationToken);
            return Results.Ok(SettingsDto.From(queue));
        })
        .WithName("GetSettings");

        app.MapPut("/api/settings", async (
            SettingsDto request,
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            if (!SettingsRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest(error!.Code, error.Message);
            }

            await settings.SetQueueSettingsAsync(parsed, cancellationToken);

            var queue = await settings.GetQueueSettingsAsync(cancellationToken);
            return Results.Ok(SettingsDto.From(queue));
        })
        .WithName("UpdateSettings");

        // Settings backup contains configuration and provider credentials but never media,
        // job, replacement, quarantine, or rollback data. Treat the downloaded JSON as secret material.
        app.MapGet("/api/settings/export", async (
            ConfigPortabilityService portability,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await portability.ExportAsync(cancellationToken);
            return Results.Ok(snapshot);
        })
        .WithName("ExportSettings");

        app.MapPost("/api/settings/import", async (
            ConfigSnapshot snapshot,
            ConfigPortabilityService portability,
            CancellationToken cancellationToken) =>
        {
            var result = await portability.ImportAsync(snapshot, cancellationToken);
            return result.Applied
                ? Results.Ok(result)
                : ApiErrors.BadRequest("settings.import.invalid", "The config file is invalid.", details: result.Errors);
        })
        .WithName("ImportSettings");

        app.MapGet("/api/queue/status", async (
            QueueDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var status = await dispatcher.GetDispatchStatusAsync(cancellationToken);
            return Results.Ok(QueueStatusDto.From(status));
        })
        .WithName("GetQueueDispatchStatus");
    }
}
