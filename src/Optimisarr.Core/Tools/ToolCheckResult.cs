namespace Optimisarr.Core.Tools;

public sealed record ToolCheckResult(
    string Name,
    string Command,
    bool Available,
    bool Required,
    string? Version,
    string? Error);
