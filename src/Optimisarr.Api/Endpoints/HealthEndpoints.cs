using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
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

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, string? adminToken, string configDirectory)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "healthy",
            service = "optimisarr",
            version = typeof(Program).Assembly.GetName().Version?.ToString(),
            checkedAt = DateTimeOffset.UtcNow
        }))
        .WithName("GetHealth");

        app.MapGet("/api/ready", async (
            OptimisarrDbContext db,
            ToolDetectionService tools,
            CancellationToken cancellationToken) =>
        {
            var failures = new List<string>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                failures.Add("database is unavailable");
            }

            foreach (var path in new[] { configDirectory, "/work", "/trash" })
            {
                if (!Directory.Exists(path) || !PathAccessProbe.CanWrite(path))
                {
                    failures.Add($"required path is not writable: {path}");
                }
            }

            var unavailableTools = (await tools.DetectAsync(cancellationToken))
                .Where(tool => !tool.Available)
                .Select(tool => tool.Name)
                .ToList();
            if (unavailableTools.Count > 0)
            {
                failures.Add($"required tools unavailable: {string.Join(", ", unavailableTools)}");
            }

            return failures.Count == 0
                ? Results.Ok(new { status = "ready" })
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: string.Join("; ", failures));
        })
        .WithName("GetReadiness");

        app.MapGet("/api/auth/status", () => Results.Ok(new
        {
            required = AdminTokenAuth.Required(adminToken)
        }))
        .WithName("GetAuthStatus");

        app.MapSettingsEndpoints();

        app.MapIntegrationEndpoints();
    }
}
