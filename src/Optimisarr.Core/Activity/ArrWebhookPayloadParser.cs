using System.Text.Json;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Activity;

/// <summary>What a Sonarr/Radarr webhook delivery asks of Optimisarr.</summary>
public abstract record ArrWebhookEvent
{
    private ArrWebhookEvent() { }

    /// <summary>The manager's "Test" button.</summary>
    public sealed record Test : ArrWebhookEvent;

    /// <summary>On Import or On Upgrade: the files, as the manager sees them, that it just put in place.</summary>
    public sealed record Import(IReadOnlyList<string> Paths) : ArrWebhookEvent;

    /// <summary>An event Optimisarr has no use for, such as Grab or Health.</summary>
    public sealed record Ignored(string EventType) : ArrWebhookEvent;

    public sealed record Invalid(string Reason) : ArrWebhookEvent;
}

/// <summary>
/// Reads the JSON a Sonarr or Radarr webhook connection sends. Both send <c>eventType</c> as the event's
/// name, and an import or upgrade as <c>Download</c>. Sonarr names the file in <c>episodeFile</c>, or, for
/// "On Import Complete", every file in <c>episodeFiles</c>; Radarr names it in <c>movieFile</c>. A file
/// without <c>path</c> is placed from its title folder (<c>series.path</c>, <c>movie.folderPath</c>) and
/// <c>relativePath</c>. Pure, so it is tested with fabricated payloads.
/// </summary>
public static class ArrWebhookPayloadParser
{
    /// <summary>More than a season pack plausibly holds; past it the delivery is refused rather than half done.</summary>
    public const int MaxFilesPerEvent = 200;

    public static ArrWebhookEvent Parse(string json, ArrConnectionType type)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new ArrWebhookEvent.Invalid("The body is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("eventType", out var eventTypeElement)
                || eventTypeElement.ValueKind != JsonValueKind.String
                || eventTypeElement.GetString() is not { Length: > 0 } eventType)
            {
                return new ArrWebhookEvent.Invalid("The body has no eventType.");
            }

            if (eventType.Equals("Test", StringComparison.OrdinalIgnoreCase))
            {
                return new ArrWebhookEvent.Test();
            }

            if (!eventType.Equals("Download", StringComparison.OrdinalIgnoreCase))
            {
                return new ArrWebhookEvent.Ignored(eventType);
            }

            var paths = type == ArrConnectionType.Radarr ? MoviePaths(root) : EpisodePaths(root);
            if (paths is null || paths.Count == 0)
            {
                return new ArrWebhookEvent.Invalid($"The import names no {(type == ArrConnectionType.Radarr ? "movie" : "episode")} file.");
            }

            if (paths.Count > MaxFilesPerEvent)
            {
                return new ArrWebhookEvent.Invalid($"The import names more than {MaxFilesPerEvent} files.");
            }

            return new ArrWebhookEvent.Import(paths);
        }
    }

    private static List<string>? EpisodePaths(JsonElement root)
    {
        var folder = StringAt(root, "series", "path");
        if (root.TryGetProperty("episodeFile", out var single))
        {
            return FilePath(single, folder) is { } path ? [path] : null;
        }

        if (!root.TryGetProperty("episodeFiles", out var many) || many.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var paths = new List<string>();
        foreach (var file in many.EnumerateArray())
        {
            if (FilePath(file, folder) is not { } path)
            {
                return null;
            }
            paths.Add(path);
        }
        return paths;
    }

    private static List<string>? MoviePaths(JsonElement root) =>
        root.TryGetProperty("movieFile", out var file) && FilePath(file, StringAt(root, "movie", "folderPath")) is { } path
            ? [path]
            : null;

    private static string? FilePath(JsonElement file, string? titleFolder)
    {
        if (file.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (String(file, "path") is { Length: > 0 } path)
        {
            return path;
        }

        return titleFolder is { Length: > 0 } && String(file, "relativePath") is { Length: > 0 } relative
            ? ArrWebhookPathResolver.Join(titleFolder, relative)
            : null;
    }

    private static string? StringAt(JsonElement root, string objectName, string property) =>
        root.TryGetProperty(objectName, out var element) && element.ValueKind == JsonValueKind.Object
            ? String(element, property)
            : null;

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
