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

internal static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        // Activity watchers: media servers Optimisarr polls so the queue pauses while
        // someone is streaming. Tokens are write-only — they are never returned.
        app.MapGet("/api/activity-watchers", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var watchers = await db.ActivityWatchers
                .AsNoTracking()
                .OrderBy(watcher => watcher.Name)
                .ToListAsync(cancellationToken);
            return Results.Ok(watchers.Select(ActivityWatcherDto.From));
        })
        .WithName("ListActivityWatchers");

        app.MapPost("/api/activity-watchers", async (
            SaveActivityWatcherRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!ActivityWatcherRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("watcher.validation", error!);
            }

            var watcher = new ActivityWatcher
            {
                Name = parsed.Name,
                Type = parsed.Type,
                BaseUrl = parsed.BaseUrl,
                ApiToken = parsed.ApiToken,
                Enabled = parsed.Enabled,
                RefreshOnReplace = parsed.RefreshOnReplace
            };
            db.ActivityWatchers.Add(watcher);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/activity-watchers/{watcher.Id}", ActivityWatcherDto.From(watcher));
        })
        .WithName("CreateActivityWatcher");

        app.MapPut("/api/activity-watchers/{id:int}", async (
            int id,
            SaveActivityWatcherRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var watcher = await db.ActivityWatchers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (watcher is null)
            {
                return ApiErrors.NotFound("watcher.notFound", $"No activity watcher with id {id}.", new { id });
            }

            if (!ActivityWatcherRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("watcher.validation", error!);
            }

            watcher.Name = parsed.Name;
            watcher.Type = parsed.Type;
            watcher.BaseUrl = parsed.BaseUrl;
            // A blank token on update keeps the stored secret rather than wiping it.
            if (parsed.ApiToken is not null)
            {
                watcher.ApiToken = parsed.ApiToken;
            }
            watcher.Enabled = parsed.Enabled;
            watcher.RefreshOnReplace = parsed.RefreshOnReplace;
            watcher.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(ActivityWatcherDto.From(watcher));
        })
        .WithName("UpdateActivityWatcher");

        app.MapDelete("/api/activity-watchers/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var watcher = await db.ActivityWatchers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (watcher is null)
            {
                return ApiErrors.NotFound("watcher.notFound", $"No activity watcher with id {id}.", new { id });
            }

            db.ActivityWatchers.Remove(watcher);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteActivityWatcher");

        // Notification targets: where Optimisarr POSTs on noteworthy events. Tokens are
        // write-only — they are never returned.
        app.MapGet("/api/notification-targets", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var targets = await db.NotificationTargets
                .AsNoTracking()
                .OrderBy(target => target.Name)
                .ToListAsync(cancellationToken);
            return Results.Ok(targets.Select(NotificationTargetDto.From));
        })
        .WithName("ListNotificationTargets");

        app.MapPost("/api/notification-targets", async (
            SaveNotificationTargetRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!NotificationTargetRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("notification.validation", error!);
            }

            var target = new NotificationTarget
            {
                Name = parsed.Name,
                Type = parsed.Type,
                Url = parsed.Url,
                Token = parsed.Token,
                Enabled = parsed.Enabled,
                NotifyOnReplacement = parsed.NotifyOnReplacement,
                NotifyOnFailure = parsed.NotifyOnFailure
            };
            db.NotificationTargets.Add(target);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/notification-targets/{target.Id}", NotificationTargetDto.From(target));
        })
        .WithName("CreateNotificationTarget");

        app.MapPut("/api/notification-targets/{id:int}", async (
            int id,
            SaveNotificationTargetRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var target = await db.NotificationTargets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (target is null)
            {
                return ApiErrors.NotFound("notification.notFound", $"No notification target with id {id}.", new { id });
            }

            if (!NotificationTargetRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("notification.validation", error!);
            }

            target.Name = parsed.Name;
            target.Type = parsed.Type;
            target.Url = parsed.Url;
            // A blank token on update keeps the stored secret rather than wiping it.
            if (parsed.Token is not null)
            {
                target.Token = parsed.Token;
            }
            target.Enabled = parsed.Enabled;
            target.NotifyOnReplacement = parsed.NotifyOnReplacement;
            target.NotifyOnFailure = parsed.NotifyOnFailure;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(NotificationTargetDto.From(target));
        })
        .WithName("UpdateNotificationTarget");

        app.MapDelete("/api/notification-targets/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var target = await db.NotificationTargets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (target is null)
            {
                return ApiErrors.NotFound("notification.notFound", $"No notification target with id {id}.", new { id });
            }

            db.NotificationTargets.Remove(target);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteNotificationTarget");

        // Sonarr/Radarr connections: managers Optimisarr asks about in-progress imports so a
        // file isn't optimised while an import is landing in its folder. Keys are write-only.
        app.MapGet("/api/arr-connections", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var connections = await db.ArrConnections
                .AsNoTracking()
                .OrderBy(connection => connection.Name)
                .ToListAsync(cancellationToken);
            return Results.Ok(connections.Select(ArrConnectionDto.From));
        })
        .WithName("ListArrConnections");

        app.MapPost("/api/arr-connections", async (
            SaveArrConnectionRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!ArrConnectionRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("arr.validation", error!);
            }

            var connection = new ArrConnection
            {
                Name = parsed.Name,
                Type = parsed.Type,
                BaseUrl = parsed.BaseUrl,
                ApiKey = parsed.ApiKey,
                Enabled = parsed.Enabled
            };
            db.ArrConnections.Add(connection);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/arr-connections/{connection.Id}", ArrConnectionDto.From(connection));
        })
        .WithName("CreateArrConnection");

        app.MapPut("/api/arr-connections/{id:int}", async (
            int id,
            SaveArrConnectionRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (connection is null)
            {
                return ApiErrors.NotFound("arr.notFound", $"No arr connection with id {id}.", new { id });
            }

            if (!ArrConnectionRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("arr.validation", error!);
            }

            connection.Name = parsed.Name;
            connection.Type = parsed.Type;
            connection.BaseUrl = parsed.BaseUrl;
            // A blank key on update keeps the stored secret rather than wiping it.
            if (parsed.ApiKey is not null)
            {
                connection.ApiKey = parsed.ApiKey;
            }
            connection.Enabled = parsed.Enabled;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(ArrConnectionDto.From(connection));
        })
        .WithName("UpdateArrConnection");

        app.MapDelete("/api/arr-connections/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (connection is null)
            {
                return ApiErrors.NotFound("arr.notFound", $"No arr connection with id {id}.", new { id });
            }

            db.ArrConnections.Remove(connection);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteArrConnection");

        // Interactive sign-in: acquire a provider token without the user pasting a raw one.
        // Each flow is start-then-poll; the client opens the auth URL / shows the code, then
        // polls until the user approves. A failure to reach the provider is a 502.
        app.MapPost("/api/connect/plex/start", async (
            ProviderConnectService connect,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var start = await connect.StartPlexAsync(cancellationToken);
                return Results.Ok(start);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream("plex.signIn.start", $"Could not start Plex sign-in: {ex.Message}");
            }
        })
        .WithName("StartPlexConnect");

        app.MapGet("/api/connect/plex/poll", async (
            long id,
            ProviderConnectService connect,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await connect.PollPlexAsync(id, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream("plex.signIn.check", $"Could not check Plex sign-in: {ex.Message}");
            }
        })
        .WithName("PollPlexConnect");

        app.MapPost("/api/connect/jellyfin/start", async (
            JellyfinConnectRequest request,
            ProviderConnectService connect,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.BaseUrl))
            {
                return ApiErrors.BadRequest("jellyfin.baseUrl.required", "Enter the Jellyfin server's base URL first.");
            }

            try
            {
                var start = await connect.StartJellyfinAsync(request.BaseUrl, cancellationToken);
                return Results.Ok(start);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream("jellyfin.quickConnect.start", $"Could not start Quick Connect: {ex.Message}");
            }
        })
        .WithName("StartJellyfinConnect");

        app.MapPost("/api/connect/jellyfin/poll", async (
            JellyfinPollRequest request,
            ProviderConnectService connect,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Secret))
            {
                return ApiErrors.BadRequest("jellyfin.quickConnect.sessionMissing", "Quick Connect session details are missing.");
            }

            try
            {
                var result = await connect.PollJellyfinAsync(request.BaseUrl, request.Secret, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream("jellyfin.quickConnect.check", $"Could not check Quick Connect: {ex.Message}");
            }
        })
        .WithName("PollJellyfinConnect");

        // Discover the Plex servers on the signed-in account so the user picks one instead of typing a URL.
        app.MapPost("/api/connect/plex/servers", async (
            PlexServersRequest request,
            ProviderConnectService connect,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return ApiErrors.BadRequest("plex.signIn.required", "Sign in with Plex first to discover servers.");
            }

            try
            {
                var servers = await connect.ListPlexServersAsync(request.Token, cancellationToken);
                return Results.Ok(servers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream("plex.servers.list", $"Could not list Plex servers: {ex.Message}");
            }
        })
        .WithName("ListPlexServers");

        // Test a connection's URL + token. When editing, the token may be blank to mean "use the stored one".
        app.MapPost("/api/connect/test", async (
            ConnectionTestRequest request,
            ProviderConnectService connect,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<ActivityWatcherType>(request.Type, ignoreCase: true, out var type))
            {
                return ApiErrors.BadRequest("watcher.type.invalid", "Type must be one of Plex, Jellyfin, or Emby.");
            }

            var token = request.Token;
            if (string.IsNullOrWhiteSpace(token) && request.Id is { } id)
            {
                token = await db.ActivityWatchers
                    .Where(watcher => watcher.Id == id)
                    .Select(watcher => watcher.ApiToken)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var result = await connect.TestAsync(type, request.BaseUrl ?? "", token ?? "", cancellationToken);
            return Results.Ok(result);
        })
        .WithName("TestConnection");
    }
}
