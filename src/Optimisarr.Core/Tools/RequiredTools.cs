namespace Optimisarr.Core.Tools;

/// <summary>
/// The tools no job can run without. Setup must not finish while one is missing: a library would
/// be scanned and offered for optimising on a server that cannot encode or probe anything.
/// </summary>
public static class RequiredTools
{
    /// <summary>Names of the required tools that did not run, in the order they were checked.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<ToolCheckResult> tools) =>
        [.. tools.Where(tool => tool.Required && !tool.Available).Select(tool => tool.Name)];
}
