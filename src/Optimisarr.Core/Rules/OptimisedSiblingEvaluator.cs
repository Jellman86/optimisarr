namespace Optimisarr.Core.Rules;

/// <summary>
/// Pure overlay that stops Optimisarr re-transcoding a file when an Optimisarr-produced copy of
/// the same title already sits beside it. When a source (e.g. an h264 <c>.mkv</c>) was optimised on
/// an earlier pass, the marked output (e.g. an hevc <c>.mp4</c>) can remain next to the original —
/// left by a move-on-complete, a cleared replacement history, or a separate re-import. The source
/// is still rule-eligible, so without this overlay it would be transcoded again only to collide with
/// the existing output when safe replacement refuses to overwrite it. Skipping it here avoids the
/// wasted encode entirely. It sits on top of the rule-based <see cref="CandidateEvaluator"/> decision,
/// like <see cref="OptimisationHistoryEvaluator"/>.
/// </summary>
public static class OptimisedSiblingEvaluator
{
    public static CandidateDecision Apply(CandidateDecision baseDecision, bool hasOptimisedSibling)
    {
        // Rule-based and history skips already carry the most specific reason; leave them alone.
        if (!baseDecision.IsEligible || !hasOptimisedSibling)
        {
            return baseDecision;
        }

        return CandidateDecision.Skipped("An optimised copy already exists alongside this file");
    }
}
