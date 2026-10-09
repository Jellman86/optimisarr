using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>A stable reference to the selected captured history, independent of download time.</summary>
internal static class DiagnosticManifestIdentifier
{
    public static string For(Guid sessionId, string scope, IEnumerable<long> eventIds) =>
        DiagnosticEventCapture.Hash($"4/{sessionId:N}/{scope}/{string.Join(',', eventIds)}")![..32];
}
