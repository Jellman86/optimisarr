namespace Optimisarr.Api.Workers;

/// <summary>
/// Where a candidate delivered by a remote worker lives while it waits to be verified, and how to
/// recognise one. The name is deliberately distinct from anything a local transcode writes, so
/// restart recovery can tell a delivered candidate mid-verification from an interrupted local
/// encode without a schema change: the former is finished work worth keeping, the latter is not.
/// </summary>
internal static class RemoteCandidate
{
    private const string Prefix = "remote-";

    /// <param name="extension">The contract's container extension, with its leading dot.</param>
    public static string PathFor(string outputRoot, int jobId, string extension) =>
        Path.Combine(outputRoot, $"{Prefix}{jobId}{extension}");

    public static bool IsDelivered(string? workOutputPath) =>
        workOutputPath is not null
        && Path.GetFileName(workOutputPath).StartsWith(Prefix, StringComparison.Ordinal);
}
