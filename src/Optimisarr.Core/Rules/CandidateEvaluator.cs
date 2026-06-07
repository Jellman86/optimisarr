using System.Globalization;

namespace Optimisarr.Core.Rules;

/// <summary>
/// Decides whether a probed file should be optimised under a set of rules, without
/// ever running FFmpeg. Pure and deterministic so the candidate list is fully unit
/// tested. Checks run cheapest-and-most-explicit first, so the returned reason is
/// the one most useful to the operator.
/// </summary>
public static class CandidateEvaluator
{
    public static CandidateDecision Evaluate(MediaProperties media, RuleSettings rules)
    {
        if (string.IsNullOrEmpty(media.VideoCodec))
        {
            return CandidateDecision.Skipped("No video stream detected (not probed, or audio-only)");
        }

        var excludedSegment = rules.ExcludePathSegments.FirstOrDefault(segment =>
            media.RelativePath.Contains(segment, StringComparison.OrdinalIgnoreCase));
        if (excludedSegment is not null)
        {
            return CandidateDecision.Skipped($"Path excluded by rule: \"{excludedSegment}\"");
        }

        if (rules.ExcludeHdr && media.IsHdr)
        {
            return CandidateDecision.Skipped("HDR / Dolby Vision excluded to avoid tone-mapping risk");
        }

        if (media.SizeBytes < rules.MinFileSizeBytes)
        {
            return CandidateDecision.Skipped(
                $"Below minimum size ({FormatSize(rules.MinFileSizeBytes)})");
        }

        if (rules.MaxHeight is { } maxHeight && media.Height is { } height && height > maxHeight)
        {
            return CandidateDecision.Skipped($"Resolution {height}p above limit ({maxHeight}p)");
        }

        // Remux/cleanup-only profile never re-encodes; it only acts on containers.
        if (rules.TargetVideoCodec is null)
        {
            var alreadyClean = media.Container is not null &&
                rules.AcceptableContainerKeywords.Any(keyword =>
                    media.Container.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            return alreadyClean
                ? CandidateDecision.Skipped("Already in a clean container")
                : CandidateDecision.Eligible($"Remux to a clean container ({media.Container} → mkv)");
        }

        if (string.Equals(media.VideoCodec, rules.TargetVideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            return CandidateDecision.Skipped($"Already {rules.TargetVideoCodec} (no expected saving)");
        }

        return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetVideoCodec}");
    }

    private static string FormatSize(long bytes)
    {
        const long megabyte = 1024L * 1024L;
        const long gigabyte = megabyte * 1024L;

        return bytes >= gigabyte
            ? $"{(bytes / (double)gigabyte).ToString("0.#", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (double)megabyte).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
