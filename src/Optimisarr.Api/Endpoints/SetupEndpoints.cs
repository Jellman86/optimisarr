using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;
using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Optimisarr.Api.Endpoints;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app, string configDirectory)
    {
        app.MapGet("/api/setup", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var state = await settings.GetSetupStateAsync(cancellationToken);
            return Results.Ok(SetupStateDto.From(state));
        })
        .WithName("GetSetupState");

        app.MapGet("/api/setup/readiness", async (
            OptimisarrDbContext db,
            ToolDetectionService tools,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var databaseAvailable = await db.Database.CanConnectAsync(cancellationToken);
            var toolResults = await tools.DetectAsync(cancellationToken);
            var paths = new[]
            {
                SetupPathDto.Probe("Config", configDirectory),
                SetupPathDto.Probe("Work", WorkPaths.Resolve(environment)),
                SetupPathDto.Probe("Quarantine", TrashPaths.Resolve(environment))
            };
            var ready = databaseAvailable
                && paths.All(path => path.Exists && path.Readable && path.Writable)
                && toolResults.All(tool => !tool.Required || tool.Available);

            return Results.Ok(new SetupReadinessDto(databaseAvailable, ready, paths, toolResults));
        })
        .WithName("GetSetupReadiness");

        app.MapPut("/api/setup/progress", async (
            SetupProgressDto request,
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var state = await settings.GetSetupStateAsync(cancellationToken);
            try
            {
                state = state.Advance(request.CompletedStep);
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.BadRequest("setup.step.invalid", exception.Message);
            }

            await settings.SetSetupStateAsync(state, cancellationToken);
            return Results.Ok(SetupStateDto.From(state));
        })
        .WithName("UpdateSetupProgress");

        app.MapPost("/api/setup/complete", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var state = await settings.GetSetupStateAsync(cancellationToken);
            try
            {
                state = state.Complete();
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.BadRequest("setup.completion.invalid", exception.Message);
            }

            await settings.SetSetupStateAsync(state, cancellationToken);
            return Results.Ok(SetupStateDto.From(state));
        })
        .WithName("CompleteSetup");

        app.MapPost("/api/setup/restart", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var state = (await settings.GetSetupStateAsync(cancellationToken)).Restart();
            await settings.SetSetupStateAsync(state, cancellationToken);
            return Results.Ok(SetupStateDto.From(state));
        })
        .WithName("RestartSetup");
    }
}

internal sealed record SetupProgressDto(int CompletedStep);

internal sealed record SetupReadinessDto(
    bool DatabaseAvailable,
    bool Ready,
    IReadOnlyList<SetupPathDto> Paths,
    IReadOnlyList<ToolCheckResult> Tools);

internal sealed record SetupPathDto(
    string Name,
    string Path,
    bool Exists,
    bool Readable,
    bool Writable)
{
    public static SetupPathDto Probe(string name, string path)
    {
        var (exists, readable, writable) = PathAccessProbe.Probe(path);
        return new SetupPathDto(name, path, exists, readable, writable);
    }
}

internal sealed record SetupStateDto(
    int Version,
    int CompletedStep,
    int CurrentStep,
    int StepCount,
    bool Completed)
{
    public static SetupStateDto From(SetupState state) => new(
        state.Version,
        state.CompletedStep,
        state.CurrentStep,
        SetupState.StepCount,
        state.Completed);
}
