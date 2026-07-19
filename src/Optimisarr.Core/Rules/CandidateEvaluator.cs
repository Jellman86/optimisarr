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

        // Track cleanup is defined only for video containers. An "Other" library can contain
        // audio and images too, but selecting this profile must never route those files through
        // their independent lossy encode pipelines.
        if (rules.Profile == RuleProfile.TrackCleanup && media.Kind != MediaKind.Video)
        {
            return CandidateDecision.Skipped("Track cleanup applies only to video files");
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

        // Animated-capable formats must have a proven single frame. ffprobe can omit nb_frames
        // for animated WebP, and treating "unknown" as a still would flatten it silently.
        if (ImageSafety.RequiresKnownFrameCount(media.VideoCodec) && media.FrameCount is null)
        {
            return CandidateDecision.Skipped("Frame count could not be proven for an animated-capable image");
        }

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

        if (ImageSafety.MayContainMultiplePages(media.VideoCodec))
        {
            return CandidateDecision.Skipped("TIFF multi-page status cannot be proven safely");
        }

        var sourceIsLossless = ImageTarget.IsLossless(media.VideoCodec);
        if (!sourceIsLossless && !rules.ReencodeLossyImages)
        {
            return CandidateDecision.Skipped(
                $"{media.VideoCodec} is already a compressed (lossy) image — left untouched");
        }

        if (ImageSafety.TargetDropsAlpha(rules.TargetImageFormat)
            && ImageSafety.MayContainAlpha(media.PixelFormat))
        {
            return CandidateDecision.Skipped("Target format cannot safely preserve the source alpha channel");
        }

        if (ImageSafety.TargetDropsHighBitDepth(rules.TargetImageFormat)
            && ImageSafety.IsHighBitDepth(media.PixelFormat, media.BitsPerRawSample))
        {
            return CandidateDecision.Skipped("Target format cannot safely preserve the source bit depth");
        }

        if (sourceIsLossless)
        {
            if (ImageSafety.TargetIsLossy(rules.TargetImageFormat) && !rules.ReencodeLossyImages)
            {
                return CandidateDecision.Skipped(
                    $"{media.VideoCodec} → {rules.TargetImageFormat} is a lossy conversion; explicit opt-in required");
            }

            return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetImageFormat}");
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

        // MP3 is the only supported target for which the FFmpeg build shipped in our final image
        // demonstrably retains an attached picture. Ogg Opus needs METADATA_BLOCK_PICTURE comment
        // translation, while the M4A/ipod muxer silently drops inherited FLAC art. Fail before
        // queueing instead of relying on the later verification gate to catch predictable loss.
        if (!rules.TargetAudioCodec.Equals("mp3", StringComparison.OrdinalIgnoreCase)
            && media.AttachedPictureCount > 0)
        {
            return CandidateDecision.Skipped(
                $"{rules.TargetAudioCodec} cannot safely preserve embedded cover art with the shipped FFmpeg; choose MP3 for this library");
        }

        if (!rules.TargetAudioCodec.Equals("aac", StringComparison.OrdinalIgnoreCase)
            && media.SubtitleTrackCount > 0)
        {
            return CandidateDecision.Skipped(
                $"{rules.TargetAudioCodec} cannot preserve the embedded timed lyrics/subtitle stream");
        }

        if (!AudioTarget.CanEncodeChannels(
                rules.TargetAudioCodec, media.MaxAudioChannels, rules.DownmixToStereo))
        {
            return CandidateDecision.Skipped(
                $"{rules.TargetAudioCodec} cannot safely encode the {media.MaxAudioChannels}-channel layout; enable stereo downmix or choose another codec");
        }

        var targetBitrate = AudioTarget.EffectiveBitrateKbps(
            rules.AudioBitrateKbps, media.MaxAudioChannels, rules.DownmixToStereo);

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

        if (!AudioTarget.LossyReencodeSaves(sourceKbps, targetBitrate))
        {
            return CandidateDecision.Skipped(
                $"{media.AudioCodec} at {sourceKbps} kbps is not far enough above the {targetBitrate} kbps target to save space — left untouched");
        }

        return CandidateDecision.Eligible(
            $"{media.AudioCodec} {sourceKbps} kbps → {rules.TargetAudioCodec} {targetBitrate} kbps");
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

        // Dolby Vision needs its dynamic-metadata RPU to render correctly. Re-encoding or tone-mapping
        // it without that RPU degrades it to HDR10/SDR at best, and a Profile 5 source (no HDR10 base
        // layer) comes out green/pink. With the perceptual gate off by default there is no backstop, so
        // a DV source is left untouched unless the library opts in — even when HDR is otherwise
        // tone-mapped or preserved, because neither path carries the DV layer.
        if (rules.TargetVideoCodec is not null
            && media.IsDolbyVision
            && !rules.OptimiseDolbyVision)
        {
            return CandidateDecision.Skipped(
                "Dolby Vision — re-encoding would drop the DV layer and risk a colour shift (Profile 5); left untouched");
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

        // Profiles with no target codec never re-encode; they act on containers and,
        // when kept-languages rules are set, on unwanted tracks. Unknown languages stay
        // conservative: no data, no removal.
        if (rules.TargetVideoCodec is null)
        {
            var audioRemovals = media.AudioLanguages is null
                ? (IReadOnlyList<int>)Array.Empty<int>()
                : Queue.AudioTrackSelection.SelectRemovals(media.AudioLanguages, rules.KeepAudioLanguages);
            var subtitleRemovals = media.SubtitleLanguages is null
                ? (IReadOnlyList<int>)Array.Empty<int>()
                : Queue.SubtitleTrackSelection.SelectRemovals(media.SubtitleLanguages, rules.KeepSubtitleLanguages);

            // Track cleanup: the only work this profile does is removing unwanted tracks,
            // so eligibility is exactly "is there anything to remove".
            if (rules.TargetContainer is null)
            {
                if (rules.KeepAudioLanguages.Count == 0 && rules.KeepSubtitleLanguages.Count == 0)
                {
                    return CandidateDecision.Skipped(
                        "No kept audio or subtitle languages configured — nothing to remove");
                }

                if (audioRemovals.Count + subtitleRemovals.Count > 0)
                {
                    return CandidateDecision.Eligible(RemovalReason(media, audioRemovals, subtitleRemovals));
                }

                var languagesUnknown =
                    (rules.KeepAudioLanguages.Count > 0 && media.AudioLanguages is null)
                    || (rules.KeepSubtitleLanguages.Count > 0 && media.SubtitleLanguages is null);
                return CandidateDecision.Skipped(languagesUnknown
                    ? "Track languages not captured yet — re-probe the file to evaluate the kept-languages rule"
                    : "No removable tracks (all tracks match the kept languages or are unknown)");
            }

            var keyword = ContainerKeyword(rules.TargetContainer);
            var alreadyClean = media.Container is not null &&
                media.Container.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            if (!alreadyClean)
            {
                return CandidateDecision.Eligible($"Remux to {rules.TargetContainer} ({media.Container} → {rules.TargetContainer})");
            }

            // A container-clean file can still carry tracks the kept-languages rules remove;
            // stripping them (a stream copy, no re-encode) is part of this profile's cleanup.
            return audioRemovals.Count + subtitleRemovals.Count > 0
                ? CandidateDecision.Eligible(RemovalReason(media, audioRemovals, subtitleRemovals))
                : CandidateDecision.Skipped($"Already in the target container ({rules.TargetContainer})");
        }

        if (string.Equals(media.VideoCodec, rules.TargetVideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            // A file already in the target codec normally has nothing to gain. But an oversized
            // same-codec file (e.g. a huge HEVC remux under an HEVC target) can still shrink a lot
            // when re-encoded at the profile's CRF, so a library may opt in by size. The size-saving
            // verification gate still rejects an output that fails to shrink, so the original is safe.
            if (rules.ReencodeSameCodecAboveBytes is { } threshold && media.SizeBytes >= threshold)
            {
                return CandidateDecision.Eligible(
                    $"Already {rules.TargetVideoCodec} but {FormatSize(media.SizeBytes)} ≥ {FormatSize(threshold)} — re-encoding to shrink");
            }

            return CandidateDecision.Skipped($"Already {rules.TargetVideoCodec} (no expected saving)");
        }

        // A source already encoded so efficiently that re-encoding it to the target codec is unlikely
        // to shrink it is skipped here, rather than transcoded and then rejected by the size-saving
        // gate (wasting GPU/CPU time). Measured as bits per pixel-second so it holds across
        // resolutions and frame rates; only applied when the profile sets a floor and the bitrate can
        // actually be measured.
        if (rules.MinSourceBitsPerPixelSecond is { } floor
            && BitsPerPixelSecond(media) is { } density
            && density < floor)
        {
            return CandidateDecision.Skipped(
                $"Already efficiently encoded ({FormatBitrate(media)} at {media.Height}p) — re-encoding is unlikely to save space");
        }

        return CandidateDecision.Eligible($"{media.VideoCodec} → {rules.TargetVideoCodec}");
    }

    // Names the languages being removed, not just counts, so the inventory and the
    // queue answer "why is this file here?" at a glance.
    private static string RemovalReason(
        MediaProperties media, IReadOnlyList<int> audioRemovals, IReadOnlyList<int> subtitleRemovals)
    {
        var parts = new List<string>(2);
        if (audioRemovals.Count > 0)
        {
            parts.Add(DescribeRemovals("audio", media.AudioLanguages!, audioRemovals));
        }
        if (subtitleRemovals.Count > 0)
        {
            parts.Add(DescribeRemovals("subtitle", media.SubtitleLanguages!, subtitleRemovals));
        }

        return $"Remove {string.Join(" + ", parts)} not in the kept languages";
    }

    private static string DescribeRemovals(string kind, IReadOnlyList<string?> languages, IReadOnlyList<int> removals) =>
        $"{removals.Count} {kind} track(s) ({string.Join(", ", removals.Select(index => languages[index] ?? "und"))})";

    // The source bitrate normalised by pixel count (bits per pixel-second): file bitrate ÷
    // (width × height). Null when the duration or resolution needed to compute it is missing, so the
    // caller stays conservative and lets the encode run. The total-file bitrate is used, which
    // slightly overstates the video bitrate and so only ever errs toward keeping a file eligible.
    private static double? BitsPerPixelSecond(MediaProperties media)
    {
        if (media.DurationSeconds is not { } duration || duration <= 0)
        {
            return null;
        }

        if (media.Width is not { } width || media.Height is not { } height || width <= 0 || height <= 0)
        {
            return null;
        }

        return media.SizeBytes * 8.0 / duration / ((double)width * height);
    }

    private static string FormatBitrate(MediaProperties media)
    {
        var megabitsPerSecond = media.SizeBytes * 8.0 / media.DurationSeconds!.Value / 1_000_000.0;
        return $"~{megabitsPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} Mbps";
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
