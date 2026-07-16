using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Setup;
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
            SettingsStore settings,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var databaseAvailable = await db.Database.CanConnectAsync(cancellationToken);
            var toolResults = await tools.DetectAsync(cancellationToken);
            var workPath = WorkPaths.Resolve(environment);
            var quarantinePath = TrashPaths.Resolve(environment);
            var minFreeDiskBytes = databaseAvailable
                ? (await settings.GetQueueSettingsAsync(cancellationToken)).MinFreeDiskBytes
                : SettingsStore.DefaultMinFreeDiskBytes;
            var libraries = databaseAvailable
                ? await db.Libraries
                    .AsNoTracking()
                    .OrderBy(library => library.Name)
                    .Select(library => new { library.Id, library.Name, library.Path })
                    .ToListAsync(cancellationToken)
                : [];

            var paths = new List<SetupPathDto>
            {
                SetupPathDto.Probe("Config", "config", configDirectory),
                SetupPathDto.Probe("Work", "work", workPath, minFreeDiskBytes),
                SetupPathDto.Probe("Quarantine", "quarantine", quarantinePath)
            };
            paths.AddRange(libraries.Select(library =>
                SetupPathDto.Probe(library.Name, "library", library.Path, libraryId: library.Id)));

            var work = paths.Single(path => path.Role == "work");
            var quarantine = paths.Single(path => path.Role == "quarantine");
            var storageRelationships = paths
                .Where(path => path.Role == "library")
                .Select(path => new SetupStorageRelationshipDto(
                    path.LibraryId!.Value,
                    path.Name,
                    SetupPathDto.SharesAtomicBoundary(path, work),
                    SetupPathDto.SharesAtomicBoundary(path, quarantine)))
                .ToList();
            var ready = databaseAvailable
                && paths.All(path => path.Issue == "none")
                && toolResults.All(tool => !tool.Required || tool.Available);

            return Results.Ok(new SetupReadinessDto(
                databaseAvailable,
                ready,
                DeploymentPlatformDetector.ToWireValue(DeploymentPlatformDetector.DetectCurrent()),
                paths,
                storageRelationships,
                toolResults));
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
    string Platform,
    IReadOnlyList<SetupPathDto> Paths,
    IReadOnlyList<SetupStorageRelationshipDto> StorageRelationships,
    IReadOnlyList<ToolCheckResult> Tools);

internal sealed record SetupPathDto(
    string Name,
    string Role,
    int? LibraryId,
    string Path,
    bool Exists,
    bool Readable,
    bool Writable,
    string Issue,
    string? FileSystemId,
    string? MountId,
    string? MountPoint,
    string? FileSystemType,
    long? AvailableBytes,
    long? TotalBytes,
    long? RequiredFreeBytes)
{
    public static SetupPathDto Probe(
        string name,
        string role,
        string path,
        long? requiredFreeBytes = null,
        int? libraryId = null)
    {
        var (exists, readable, writable) = PathAccessProbe.Probe(path);
        var evidence = FileSystemEvidenceProbe.Probe(path);
        var issue = SetupPathIssueClassifier.Classify(
            exists,
            readable,
            writable,
            evidence.AvailableBytes,
            requiredFreeBytes);
        return new SetupPathDto(
            name,
            role,
            libraryId,
            path,
            exists,
            readable,
            writable,
            SetupPathIssueClassifier.ToWireValue(issue),
            evidence.FileSystemId,
            evidence.MountId,
            evidence.MountPoint,
            evidence.FileSystemType,
            evidence.AvailableBytes,
            evidence.TotalBytes,
            requiredFreeBytes);
    }

    public static bool? SharesAtomicBoundary(SetupPathDto first, SetupPathDto second) =>
        string.IsNullOrWhiteSpace(first.MountId) || string.IsNullOrWhiteSpace(second.MountId)
            ? null
            : string.Equals(first.MountId, second.MountId, StringComparison.Ordinal);
}

internal sealed record SetupStorageRelationshipDto(
    int LibraryId,
    string LibraryName,
    bool? WorkAtomic,
    bool? QuarantineAtomic);

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
