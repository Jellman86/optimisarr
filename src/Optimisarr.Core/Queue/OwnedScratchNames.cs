namespace Optimisarr.Core.Queue;

/// <summary>
/// How a job's owned scratch workspace is named and marked. The folder name carries the job and a
/// random nonce, and the marker file inside holds the same nonce, so a later pass can recognise
/// the job's own workspace without trusting any other folder in the work tree.
/// </summary>
public static class OwnedScratchNames
{
    public const string MarkerFileName = ".optimisarr-owner";
    public static IReadOnlyList<string> Purposes { get; } = ["adaptive-quality", "verification-reference"];

    public static string DirectoryName(string purpose, int jobId, string nonce) => $".{purpose}-{jobId}-{nonce}";

    /// <summary>The nonce when the folder name is one of this job's workspaces, otherwise null.</summary>
    public static string? NonceFor(string directoryName, int jobId)
    {
        foreach (var purpose in Purposes)
        {
            var prefix = $".{purpose}-{jobId}-";
            if (!directoryName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var nonce = directoryName[prefix.Length..];
            return nonce.Length == 32 && nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? nonce : null;
        }
        return null;
    }
}
