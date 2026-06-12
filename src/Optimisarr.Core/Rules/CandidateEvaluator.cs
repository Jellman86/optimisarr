using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

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
        // A file Optimisarr has already produced carries its fingerprint in the container
        // metadata. Honour it first and unconditionally: the mark travels with the file, so
        // this holds even on another machine or after the queue history has been cleared.
        if (!string.IsNullOrWhiteSpace(media.OptimisedMarker))
        {
            return CandidateDecision.Skipped("Already optimised by Optimisarr (file is tagged)");
        }

        // Each media kind has its own eligibility rules.
        return media.Kind switch
        {
            MediaKind.Audio => EvaluateAudio(media, rules),
            MediaKind.Image => EvaluateImage(media, rules),
            _ => EvaluateVideo(media, rules)
        };
    }

    private static CandidateDecision EvaluateImage(MediaProperties media, RuleSettings rules)
    {
        // An image's still-picture codec is captured as the file's VideoCodec by the probe.
        if (string.IsNullOrEmpty(media.VideoCodec))
        {
            return CandidateDecision.Skipped("No image data detected");
        }

        // An animated image (e.g. an animated GIF or WebP) is really a short video; the still
        // pipeline would flatten it to a broken single-frame output, so leave it untouched.
        if (media.FrameCount is > 1)
        {
            return CandidateDecision.Skipped($"Animated image ({media.FrameCount} frames) — not optimised");
        }

        if (media.SizeBytes < ImageTarget.MinFileSizeBytes)
        {
            return CandidateDecision.Skipped($"Below minimum size ({FormatSize(ImageTarget.MinFileSizeBytes)})");
        }

        if (ImageTarget.IsAlreadyInFormat(media.VideoCodec, rules.TargetImageFormat))
        {
            return CandidateDecision.Skipped($"Already {rules.TargetImageFormat} (no expected saving)");
        }

        // Lossless sources (PNG/BMP/TIFF/GIF) are always worth re-encoding: a large saving with no
        // quality loss.
        if (ImageTarget.IsLossless(media.VideoCodec))
        {
            return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetImageFormat}");
        }

        // The source is already a lossy/efficient image (e.g. a JPEG). By default it is left
        // untouched, since re-encoding adds generational loss for a smaller gain.
        if (!rules.ReencodeLossyImages)
        {
            return CandidateDecision.Skipped($"{media.VideoCodec} is already a compressed (lossy) image — left untouched");
        }

        return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetImageFormat}");
    }

    private static CandidateDecision EvaluateAudio(MediaProperties media, RuleSettings rules)
    {
        if (string.IsNullOrEmpty(media.AudioCodec))
        {
            return CandidateDecision.Skipped("No audio stream detected");
        }

        if (media.SizeBytes < AudioTarget.MinFileSizeBytes)
        {
            return CandidateDecision.Skipped($"Below minimum size ({FormatSize(AudioTarget.MinFileSizeBytes)})");
        }

        if (string.Equals(media.AudioCodec, rules.TargetAudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            return CandidateDecision.Skipped($"Already {rules.TargetAudioCodec} (no expected saving)");
        }

        // Lossless sources are always worth re-encoding: a large saving with no audible loss.
        if (AudioTarget.IsLossless(media.AudioCodec))
        {
            return CandidateDecision.Eligible($"{media.AudioCodec} → {rules.TargetAudioCodec}");
        }

        // The source is already lossy. By default it is left untouched, since re-encoding lossy
        // audio adds generational loss for little gain.
        if (!rules.ReencodeLossyAudio)
        {
            return CandidateDecision.Skipped($"{media.AudioCodec} is already a space-efficient (lossy) codec — left untouched");
        }

        // The library has opted in to re-encoding lossy audio, but only when it genuinely saves
        // space. Without a known source bitrate we cannot prove a saving, so stay conservative.
        if (media.AudioBitrateKbps is not { } sourceKbps)
        {
            return CandidateDecision.Skipped($"{media.AudioCodec} source bitrate unknown — cannot confirm a saving, left untouched");
        }

        if (!AudioTarget.LossyReencodeSaves(sourceKbps, rules.AudioBitrateKbps))
        {
            return CandidateDecision.Skipped(
                $"{media.AudioCodec} at {sourceKbps} kbps is not far enough above the {rules.AudioBitrateKbps} kbps target to save space — left untouched");
        }

        return CandidateDecision.Eligible(
            $"{media.AudioCodec} {sourceKbps} kbps → {rules.TargetAudioCodec} {rules.AudioBitrateKbps} kbps");
    }

    private static CandidateDecision EvaluateVideo(MediaProperties media, RuleSettings rules)
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

        if (rules.Hdr == HdrHandling.Exclude && media.IsHdr)
        {
            return CandidateDecision.Skipped("HDR / Dolby Vision excluded by this library's rules");
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
            var keyword = ContainerKeyword(rules.TargetContainer);
            var alreadyClean = media.Container is not null &&
                media.Container.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            return alreadyClean
                ? CandidateDecision.Skipped($"Already in the target container ({rules.TargetContainer})")
                : CandidateDecision.Eligible($"Remux to {rules.TargetContainer} ({media.Container} → {rules.TargetContainer})");
        }

        if (string.Equals(media.VideoCodec, rules.TargetVideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            return CandidateDecision.Skipped($"Already {rules.TargetVideoCodec} (no expected saving)");
        }

        return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetVideoCodec}");
    }

    // ffprobe reports the demuxer's format_name (e.g. "matroska,webm"), which uses
    // long names rather than the file extension the operator picks as the target.
    private static string ContainerKeyword(string targetContainer) =>
        targetContainer.Trim().ToLowerInvariant() switch
        {
            "mkv" => "matroska",
            "mka" => "matroska",
            "m4v" => "mp4",
            var other => other
        };

    private static string FormatSize(long bytes)
    {
        const long megabyte = 1024L * 1024L;
        const long gigabyte = megabyte * 1024L;

        return bytes >= gigabyte
            ? $"{(bytes / (double)gigabyte).ToString("0.#", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (double)megabyte).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
