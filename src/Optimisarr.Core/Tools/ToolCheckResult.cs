namespace Optimisarr.Core.Tools;

public sealed record ToolCheckResult(
    string Name,
    string Command,
    bool Available,
    string? Version,
    string? Error);
