using Optimisarr.Core.Tools;

namespace Optimisarr.Api.Setup;

/// <summary>Says which required tools are missing on this server right now.</summary>
public interface IRequiredToolsGate
{
    Task<IReadOnlyList<string>> MissingAsync(CancellationToken cancellationToken);
}

internal sealed class RequiredToolsGate(ToolDetectionService tools) : IRequiredToolsGate
{
    public async Task<IReadOnlyList<string>> MissingAsync(CancellationToken cancellationToken) =>
        RequiredTools.Missing(await tools.DetectAsync(cancellationToken));
}
