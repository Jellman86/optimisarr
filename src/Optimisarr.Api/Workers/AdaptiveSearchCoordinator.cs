using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Workers;

/// <summary>What one reported candidate left the search knowing, and what it wants next.</summary>
internal sealed record AdaptiveSearchProgress(
    IReadOnlyList<AdaptiveQualityProbe> Probes,
    AdaptiveQualityDecision Decision);

/// <summary>
/// Judges a measurement a worker took, and decides what it measures next.
///
/// <para>The judgement is the same one a local search makes — pool the windows, hold the pooled
/// scores against the library's gate — so a candidate means the same thing whichever machine
/// encoded it. The decision is <see cref="AdaptiveQualitySearch"/>, unchanged and in one place,
/// because the search brackets and bisects: a worker choosing its own next candidate would be
/// running a different search from the one the library's quality was defined against.</para>
/// </summary>
internal static class AdaptiveSearchCoordinator
{
    /// <summary>
    /// Returns null when the report cannot be turned into evidence: a quality nobody asked for, or
    /// logs that do not carry a usable score. Both mean the search learns nothing from this
    /// exchange, and inventing a probe would be worse than admitting that — the search would go on
    /// to bracket around a measurement that never happened.
    /// </summary>
    public static AdaptiveSearchProgress? Advance(
        int baselineQuality,
        IReadOnlyList<AdaptiveQualityProbe> priorProbes,
        int asked,
        AdaptiveSearchReport report,
        RemoteQualityContract contract,
        VerificationPolicy policy)
    {
        // The worker answers the question it was asked or it answers nothing. Accepting another
        // value would let it walk the search somewhere the control plane never chose, and every
        // later candidate is derived from this one.
        if (report.Quality != asked)
        {
            return null;
        }

        if (report.Logs.Count != contract.WindowCount)
        {
            return null;
        }

        // A split that does not add up to the total, or does not cover every window, describes
        // some other set of samples. Judging sizes on it would misplace which scenes grew.
        if (report.WindowEncodedBytes is { } split
            && (split.Count != contract.WindowCount
                || split.Any(bytes => bytes <= 0)
                || split.Sum() != report.EncodedBytes))
        {
            return null;
        }

        var windows = new List<QualityResult>(report.Logs.Count);
        foreach (var log in report.Logs)
        {
            var parsed = string.IsNullOrWhiteSpace(log) ? null : QualityScoreParser.Parse(log);
            if (parsed is null)
            {
                return null;
            }

            // The model is the server's, not the worker's word for it: the same file scores
            // differently under the HD and 4K models, so an unlabelled score cannot be held
            // against a threshold at all.
            windows.Add(QualityResult.Ok(parsed with { ModelVersion = contract.Model }));
        }

        var pooled = QualityScoreAggregator.Combine(windows, contract.Sampling);
        if (!pooled.Measured || pooled.Scores is null)
        {
            return null;
        }

        // Replacing rather than appending. A repeated report for the same candidate — a retried
        // request, a worker that did not see the response — would otherwise grow this list for
        // ever, and it is persisted between exchanges. `AdaptiveQualitySearch` already takes the
        // last of any duplicates when it decides, so keeping only the last here means the stored
        // evidence and the decision are looking at the same thing.
        var probes = priorProbes
            .Where(probe => probe.Quality != report.Quality)
            .Append(new AdaptiveQualityProbe(
                report.Quality,
                VmafSoftwareConfirmation.MeetsGate(pooled.Scores, policy),
                report.EncodedBytes,
                pooled.Scores,
                report.WindowEncodedBytes))
            .ToList();

        return new AdaptiveSearchProgress(probes, AdaptiveQualitySearch.Decide(baselineQuality, probes));
    }
}
