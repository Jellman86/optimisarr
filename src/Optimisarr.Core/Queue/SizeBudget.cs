namespace Optimisarr.Core.Queue;

/// <summary>
/// A byte threshold for a candidate that can no longer pass the required size-saving gate.
/// The caller still applies the normal verification policy to every completed candidate.
/// </summary>
public static class SizeBudget
{
    public static long? MaxCandidateBytes(long sourceBytes, bool requireReduction, bool disposable) =>
        requireReduction && !disposable && sourceBytes > 0 ? sourceBytes - 1 : null;

    public static bool Exceeded(long candidateBytes, long maxCandidateBytes) =>
        candidateBytes > maxCandidateBytes;
}
