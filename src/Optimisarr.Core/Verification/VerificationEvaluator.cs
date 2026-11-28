using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Library;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure verification: turns a gathered <see cref="VerificationInput"/> into a
/// <see cref="VerificationReport"/>. No I/O, no FFmpeg — every check is a
/// deterministic comparison so it can be unit tested without media on disk.
/// </summary>
public static class VerificationEvaluator
{
    public const string SourceVideoTimelineCheckName = "Source video timeline";
    public const string DolbyVisionKeptCheckName = "Dolby Vision kept";
    public const string EmbeddedArtworkCheckName = "Embedded artwork";
    // Half a percent covers the rounding between a planned rate (59.94 / 2) and ffprobe's rational
    // for the same cadence (30000/1001), while staying far under the factor of two a missed
    // decimation would show.
    private const double FrameRateTolerance = 0.005;

    public static VerificationReport Evaluate(VerificationInput input, VerificationPolicy policy)
    {
        var isImage = input.Kind == MediaKind.Image;
        // Video-stream and picture-integrity gates only apply to a video job; an audio job
        // has no video to check (any cover art is incidental) and a still has no time axis.
        var isVideo = input.Kind == MediaKind.Video;

        var checks = new List<VerificationCheck>
        {
            DecodeHealth(input),
            OutputReadable(input)
        };

        // Duration and track-retention gates apply to time-based media (video/audio); a still
        // has no duration and no audio/subtitle tracks to compare.
        if (!isImage)
        {
            checks.Add(DurationWithinTolerance(input, policy));
            if (input.SilenceTrim is { } trim) checks.Add(SilenceTrimmedAsPlanned(input, trim));
            if (input.KeptAudio is { } kept) checks.Add(KeptAudioUnchanged(kept));
            if (input.TagsKept is { } tags) checks.Add(TagsKeptUnchanged(tags));
            checks.Add(AudioRetained(input, policy));
            checks.Add(SubtitlesRetained(input, policy));

            if (input.ExpectedAudioLanguages is not null)
            {
                checks.Add(LanguagesRetained(
                    "Audio languages", input.ExpectedAudioLanguages, input.OutputAudioLanguages));
            }

            if (input.ExpectedSubtitleLanguages is not null)
            {
                checks.Add(LanguagesRetained(
                    "Subtitle languages", input.ExpectedSubtitleLanguages, input.OutputSubtitleLanguages));
            }

            // Only registered when the job promised an unchanged container (track cleanup),
            // so ordinary remuxes/transcodes don't get a noisy not-applicable line.
            if (input.RequireContainerUnchanged)
            {
                checks.Add(ContainerUnchanged(input));
                checks.Add(AudioCodecsPreserved(input));
            }
        }

        // Music metadata and embedded artwork are part of the media, not decoration. Audio-only
        // jobs must prove both survived before their source can be replaced.
        if (input.Kind == MediaKind.Audio)
        {
            checks.Add(AudioMetadataPreserved(input));
            if (input.ExpectedAudioCodec is { } expected)
                checks.Add(input.OutputAudioCodecs is { Count: > 0 } codecs
                    && codecs.All(codec => string.Equals(codec, expected, StringComparison.OrdinalIgnoreCase))
                    ? Pass("Audio codec", $"All output audio streams use {expected}.")
                    : Fail("Audio codec", $"Expected {expected} on every output audio stream."));
        }

        checks.Add(SizeReduced(input, policy));
        if (isVideo && input.VideoReencoded && policy.RequireSizeReduction
            && policy.MaximumSizeSavingPercent is > 0)
        {
            checks.Add(CompressionCeiling(input, policy.MaximumSizeSavingPercent.Value));
        }

        // A still is verified as an image: it must contain a picture and keep its dimensions.
        // No downscaling is performed yet, so any shrink is an unintended/degenerate encode.
        if (isImage)
        {
            checks.Add(PicturePresent(input));
            if (input.ExpectedVideoCodec is { } expectedImageCodec)
                checks.Add(string.Equals(input.OutputVideoCodec, expectedImageCodec, StringComparison.OrdinalIgnoreCase)
                    ? Pass("Image format", $"The output uses the requested {expectedImageCodec} format.")
                    : Fail("Image format", $"Expected {expectedImageCodec}; the output reports {input.OutputVideoCodec ?? "no picture codec"}."));
            checks.Add(DimensionsRetained(input));

            // The structural-quality (SSIM) gate is the still-image counterpart of VMAF and
            // contributes when enabled (the safe default); otherwise the report is unchanged.
            if (policy.ImageQualityGateEnabled)
            {
                checks.Add(ImageQuality(input, policy));
            }

            // The metadata gate fails an image whose re-encode silently dropped the source's
            // ICC colour profile or EXIF; enabled by default, and only flags loss (never a gain).
            if (policy.ImageMetadataGateEnabled)
            {
                checks.Add(ImageMetadataPreserved(input));
            }
        }

        if (isVideo)
        {
            checks.Add(VideoStreamPresent(input));
            if (input.RequireVideoFrameRetention && input.ExpectedFrameRate is null)
            {
                checks.Add(input.OriginalDecodedFrameCount is > 0 && input.OutputDecodedFrameCount is > 0
                    ? input.OriginalDecodedFrameCount == input.OutputDecodedFrameCount
                        ? Pass("Picture retention", $"Source and output each have {input.OriginalDecodedFrameCount} decoded pictures. Counts match.")
                        : Fail("Picture retention", $"Original has {input.OriginalDecodedFrameCount} decoded pictures; output has {input.OutputDecodedFrameCount}. Pictures were lost or added.")
                    : Fail("Picture retention", "Complete decoded picture counts are unavailable; preservation could not be verified."));
            }
            checks.Add(VideoStructurePreserved(input));
            if (input.RequirePixelShapeRetained)
            {
                checks.Add(PixelShapePreserved(input));
            }

            // Cover art is part of the file, not decoration. FFmpeg can leave an MP4's covr out, or copy a
            // Matroska cover in as an ordinary video track, without an error, and a super-resolution
            // output carries only the upscaled picture (#598).
            if (input.OriginalAttachedPictureCount > 0)
            {
                checks.Add(VideoArtworkPreserved(input));
            }
        }

        // HDR preservation only matters when the original carries an HDR signal, so
        // SDR sources don't get a noisy not-applicable line.
        if (isVideo && input.OriginalIsHdr)
        {
            checks.Add(HdrPreserved(input));
        }

        // A stream copy keeps the Dolby Vision RPU in the video packets, but the configuration record
        // that tells a player to use it lives in the container, and a muxer can leave it out without
        // an error. A re-encode reaches Dolby Vision only when the library opted in to losing the layer.
        if (isVideo && !input.VideoReencoded && (input.OriginalIsDolbyVision || input.OriginalDolbyVision is not null))
        {
            checks.Add(DolbyVisionKept(input));
        }

        // Audio fidelity (no silent downmix or sample-rate drop) is checked when audio
        // retention is required and the original's audio shape is known.
        if (policy.RequireAudioRetained && input.OriginalMaxAudioChannels > 0)
        {
            checks.Add(AudioFidelity(input));
        }

        // A tone-mapped encode has an explicit Rec.709 target even if its HDR source omitted tags.
        // Otherwise compare only when the original declared a colour domain.
        if (isVideo
            && (input.HdrConvertedToSdr
                || input.OriginalColorPrimaries is not null
                || input.OriginalColorTransfer is not null
                || input.OriginalColorSpace is not null
                || input.OriginalColorRange is not null))
        {
            checks.Add(ColorMetadataPreserved(input));
        }

        // A/V sync is only meaningful when both stream start times are known.
        if (isVideo && input.OutputVideoStartSeconds is not null && input.OutputAudioStartSeconds is not null)
        {
            checks.Add(AvSync(input));
        }

        // Timestamp monotonicity is checked whenever we managed to read the output's
        // packet timestamps; an unreadable packet stream simply omits the line.
        if ((isVideo || input.Kind == MediaKind.Audio) && input.TimestampsMeasured)
        {
            checks.Add(MonotonicTimestamps(input));
        }

        // Keep malformed source timelines distinct from output truncation. Primary audio that
        // materially outlasts the final video packet is meaningful evidence of a damaged picture
        // stream; container duration is not, because subtitles and attachments may end much later.
        if (isVideo
            && input.OriginalTimestampsMeasured
            && input.OriginalLastPresentationSeconds is not null
            && input.OriginalAudioLastPresentationSeconds is not null)
        {
            checks.Add(SourceVideoTimelineComplete(input));
        }

        // A truncated/partial last GOP shows up as the output's video ending well before
        // the source runtime. It needs the source duration and the output's real last
        // presentation time, so it is checked only when both are known.
        if (isVideo
            && input.TimestampsMeasured
            && input.OutputLastPresentationSeconds is not null
            && !input.SourceTimelineIndeterminate
            && OriginalVideoSpanSeconds(input) is > 0)
        {
            checks.Add(TailComplete(input));
        }

        // Remuxes copy the encoded frames unchanged, so VMAF is both redundant and expensive.
        // Non-video media use their applicable audio/image gates instead.
        var vmafRequested = policy.RequiresVmaf(input.Kind, input.VideoReencoded);
        if (vmafRequested && policy.QualityGateEnabled)
        {
            checks.Add(PerceptualQuality(input, policy));
        }

        if (!isImage && policy.AudioLoudnessGateEnabled)
        {
            checks.Add(LoudnessPreserved(input, policy));
        }

        if (!isImage && policy.AudioClippingGateEnabled)
        {
            checks.Add(NoClippingIntroduced(input, policy));
        }

        return new VerificationReport(
            checks,
            Vmaf: vmafRequested
                ? new VmafEvidence(input.QualityMeasured, input.QualityError, input.QualityScores)
                : null,
            Colour: isVideo ? ColourContract(input) : null);
    }

    private static ColourEvidence ColourContract(VerificationInput input)
    {
        var source = new ColourTags(input.OriginalColorPrimaries, input.OriginalColorTransfer,
            input.OriginalColorSpace, input.OriginalColorRange);
        var expected = input.HdrConvertedToSdr
            ? new ColourTags("bt709", "bt709", "bt709", "tv")
            : source;
        var output = new ColourTags(input.OutputColorPrimaries, input.OutputColorTransfer,
            input.OutputColorSpace, input.OutputColorRange);
        return new ColourEvidence(source, expected, output, input.HdrConvertedToSdr);
    }

    private static VerificationCheck AudioMetadataPreserved(VerificationInput input)
    {
        const string name = "Audio metadata and artwork";
        if (input.OutputAttachedPictureCount < input.OriginalAttachedPictureCount)
        {
            return Fail(name,
                $"Embedded artwork dropped from {input.OriginalAttachedPictureCount} to {input.OutputAttachedPictureCount} picture(s).");
        }

        if (input.Mp4Tags is { } mp4 && Mp4TagProblem(mp4) is { } problem)
        {
            return Fail(name, problem);
        }

        // One name per tag, so a FLAC's MUSICBRAINZ_ALBUMID and the M4A item it becomes compare as one.
        var original = AudioFormatTags.Normalise(input.OriginalFormatTags);
        var output = AudioFormatTags.Normalise(input.OutputFormatTags);
        foreach (var (key, tag) in original)
        {
            if (!output.TryGetValue(key, out var kept))
            {
                return Fail(name, $"Audio metadata tag '{tag.Name}' was lost.");
            }

            if (!string.Equals(tag.Value, kept.Value, StringComparison.Ordinal))
            {
                return Fail(name, $"Audio metadata tag '{tag.Name}' changed during conversion.");
            }
        }

        return Pass(name,
            $"Retained {original.Count} source metadata tag(s) and {input.OriginalAttachedPictureCount} embedded picture(s)"
            + (input.Mp4Tags is null ? "." : ", stored as iTunes tags."));
    }

    /// <summary>
    /// Why an M4A's tags fail, or null. ffprobe reads QuickTime keys as readily as iTunes atoms, so
    /// the file's own atoms are read: they must be iTunes atoms, and an M4A source's items must all be
    /// there unchanged, apart from those written again for the new file.
    /// </summary>
    private static string? Mp4TagProblem(Mp4TagEvidence evidence)
    {
        if (evidence.Output?.Tags is not { } output)
            return $"The new file's tags could not be read: {evidence.Output?.Problem ?? "not measured"}.";
        if (output.Layout == Mp4TagLayout.QuickTimeKeys)
            return "The new file's tags are stored as QuickTime keys, which many music players can't read, instead of iTunes tags.";
        if (evidence.Source is not { } sourceRead) return LanguageProblem(output, evidence.SourceLanguage);
        if (sourceRead.Tags is not { } source)
            return $"The original's tags could not be read: {sourceRead.Problem}.";
        if (source.Layout != Mp4TagLayout.Itunes) return LanguageProblem(output, evidence.SourceLanguage);

        var sourceItems = source.Items.Where(item => !Mp4TagCarryOver.Regenerated(item.Key)).ToList();
        var outputItems = output.Items.Where(item => !Mp4TagCarryOver.Regenerated(item.Key)).ToList();
        var sourceKeys = sourceItems.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var outputKeys = outputItems.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var lost = sourceKeys.Where(key => !outputKeys.Contains(key)).ToList();
        var gained = outputKeys.Where(key => !sourceKeys.Contains(key)).ToList();
        var changed = sourceKeys.Where(key => outputKeys.Contains(key)
            && !SameItems(sourceItems.Where(item => item.Key == key), outputItems.Where(item => item.Key == key))).ToList();
        if (lost.Count == 0 && gained.Count == 0 && changed.Count == 0) return null;
        var parts = new List<string>();
        if (lost.Count > 0) parts.Add($"lost {Names(lost)}");
        if (changed.Count > 0) parts.Add($"changed {Names(changed)}");
        if (gained.Count > 0) parts.Add($"gained {Names(gained)}");
        return $"The iTunes tags {string.Join(" and ", parts)}.";

        static string Names(List<string> keys) =>
            string.Join(", ", keys.Take(8).Select(key => key.Length > 40 ? key[..40] + "…" : key)) + (keys.Count > 8 ? $" and {keys.Count - 8} more" : "");
    }

    /// <summary>
    /// Why a converted M4A does not hold the language its source's tags state, or null. ffprobe
    /// shows every M4A's audio track as language "und", and the comparison of ffprobe's tags leaves
    /// language out, so the iTunes item itself is compared. An M4A source's items are compared whole.
    /// </summary>
    private static string? LanguageProblem(Mp4Tags output, string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage)) return null;
        var languages = output.Items.Where(item => item.Key == Mp4TagCarryOver.LanguageKey).ToList();
        if (languages.Count == 0) return "Audio metadata tag 'LANGUAGE' was lost.";
        return languages is [var language] && Mp4TagCarryOver.HoldsText(language, sourceLanguage.Trim())
            ? null
            : "Audio metadata tag 'LANGUAGE' changed during conversion.";
    }

    private static bool SameItems(IEnumerable<Mp4TagItem> source, IEnumerable<Mp4TagItem> output)
    {
        var remaining = output.ToList();
        foreach (var item in source)
        {
            if (!remaining.Remove(item)) return false;
        }
        return remaining.Count == 0;
    }

    private static VerificationCheck NoClippingIntroduced(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: an enabled gate that could not measure the true peak blocks replacement.
        if (!input.TruePeakMeasured || !TruePeakSafety.Known(input.OriginalTruePeakDbtp, input.OriginalTruePeakIsSilent)
            || !TruePeakSafety.Known(input.OutputTruePeakDbtp, input.OutputTruePeakIsSilent) || !double.IsFinite(policy.MaxTruePeakDbtp))
        {
            return Fail("Audio clipping (true peak)", $"True peak could not be measured: {Describe(input.TruePeakError)}");
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0}, output {1} (ceiling {2:0.#} dBTP).",
            input.OriginalTruePeakIsSilent ? "silence" : input.OriginalTruePeakDbtp!.Value.ToString("0.#", CultureInfo.InvariantCulture) + " dBTP",
            input.OutputTruePeakIsSilent ? "silence" : input.OutputTruePeakDbtp!.Value.ToString("0.#", CultureInfo.InvariantCulture) + " dBTP", policy.MaxTruePeakDbtp);

        // Clipping is only "introduced" when the output rises above the ceiling while the
        // original stayed at or below it — an already-hot source isn't the re-encode's fault.
        // A small margin absorbs measurement noise so an unchanged level never trips the gate.
        var introducedClipping = TruePeakSafety.Introduced(input.OriginalTruePeakDbtp, input.OriginalTruePeakIsSilent,
            input.OutputTruePeakDbtp, input.OutputTruePeakIsSilent, policy.MaxTruePeakDbtp);

        return introducedClipping
            ? Fail("Audio clipping (true peak)", $"{detail} The re-encode pushed the true peak above the ceiling.")
            : Pass("Audio clipping (true peak)", detail);
    }

    private static VerificationCheck LoudnessPreserved(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: an enabled gate that could not measure loudness blocks replacement.
        if (!input.LoudnessMeasured || input.OriginalLoudnessLufs is not { } original || input.OutputLoudnessLufs is not { } output)
        {
            return Fail("Audio loudness (EBU R128)", $"Loudness could not be measured: {Describe(input.LoudnessError)}");
        }

        var drift = Math.Abs(original - output);
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:0.#} LUFS, output {1:0.#} LUFS ({2:0.##} LU drift, tolerance {3:0.##} LU).",
            original, output, drift, policy.MaxLoudnessDriftLufs);

        return drift <= policy.MaxLoudnessDriftLufs
            ? Pass("Audio loudness (EBU R128)", detail)
            : Fail("Audio loudness (EBU R128)", detail);
    }

    private static VerificationCheck ColorMetadataPreserved(VerificationInput input)
    {
        var evidence = ColourContract(input);
        var values = $"Source {FormatColourTags(evidence.Source)}; expected {FormatColourTags(evidence.Expected)}; "
            + $"output {FormatColourTags(evidence.Output)}.";
        if (input.HdrConvertedToSdr)
        {
            // The shared production tone-map deliberately converts BT.2020/PQ or
            // HLG into limited-range Rec.709. Comparing output tags with the HDR
            // source would reject the intended conversion; validate the declared
            // SDR domain instead so stale HDR tags still fail safely.
            var unexpected = new List<string>();
            AddUnexpectedToneMapValue(unexpected, "primaries", input.OutputColorPrimaries);
            AddUnexpectedToneMapValue(unexpected, "transfer", input.OutputColorTransfer);
            AddUnexpectedToneMapValue(unexpected, "matrix", input.OutputColorSpace);
            if (input.OutputColorRange is not null
                && !string.Equals(input.OutputColorRange, "tv", StringComparison.OrdinalIgnoreCase))
            {
                unexpected.Add($"range is {input.OutputColorRange}, expected tv");
            }

            return unexpected.Count == 0
                ? Pass("Colour metadata", $"Intentional HDR-to-SDR output is tagged as Rec.709. {values}")
                : Fail("Colour metadata", $"Tone-mapped SDR metadata is invalid: {string.Join("; ", unexpected)}. {values}");
        }

        // Only a definite change is a failure: the original and output both declare a
        // value and they differ (e.g. BT.709 re-tagged as BT.601). A dropped tag on the
        // output is treated as benign, since absence usually means "container default".
        var mismatches = new List<string>();
        AddMismatch(mismatches, "primaries", input.OriginalColorPrimaries, input.OutputColorPrimaries);
        var equivalentSdTransfer = !input.OriginalIsHdr
            && ((string.Equals(input.OriginalColorTransfer, "smpte170m", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(input.OutputColorTransfer, "bt709", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(input.OriginalColorTransfer, "bt709", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(input.OutputColorTransfer, "smpte170m", StringComparison.OrdinalIgnoreCase)));
        // H.262 gives SMPTE 170M and BT.709 the same transfer function. VideoToolbox can emit
        // the BT.709 name while preserving the SD primaries and matrix; this is a tag alias,
        // not an HDR-to-SDR conversion. All other transfer changes remain definite mismatches.
        if (!equivalentSdTransfer)
        {
            AddMismatch(mismatches, "transfer", input.OriginalColorTransfer, input.OutputColorTransfer);
        }
        AddMismatch(mismatches, "matrix", input.OriginalColorSpace, input.OutputColorSpace);
        AddMismatch(mismatches, "range", input.OriginalColorRange, input.OutputColorRange);

        var aliasDetail = equivalentSdTransfer
            ? " SMPTE 170M and BT.709 transfer tags describe the same curve."
            : string.Empty;
        return mismatches.Count == 0
            ? Pass("Colour metadata", $"Colour primaries, transfer, matrix, and range preserved.{aliasDetail} {values}")
            : Fail("Colour metadata", $"Colour metadata changed: {string.Join("; ", mismatches)}. {values}");
    }

    private static string FormatColourTags(ColourTags tags) =>
        $"primaries={tags.Primaries ?? "unknown"}, transfer={tags.Transfer ?? "unknown"}, "
        + $"matrix={tags.Matrix ?? "unknown"}, range={tags.Range ?? "unknown"}";

    private static void AddUnexpectedToneMapValue(List<string> unexpected, string label, string? output)
    {
        if (output is not null && !string.Equals(output, "bt709", StringComparison.OrdinalIgnoreCase))
        {
            unexpected.Add($"{label} is {output}, expected bt709");
        }
    }

    private static void AddMismatch(List<string> mismatches, string label, string? original, string? output)
    {
        if (original is not null && output is not null
            && !string.Equals(original, output, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"{label} {original} → {output}");
        }
    }

    private static VerificationCheck MonotonicTimestamps(VerificationInput input)
    {
        // Decode timestamps that step backward mean the output's packets are out of
        // order — the file may decode yet stall or desync on playback, so it is not a
        // safe replacement.
        if (input.NonMonotonicTimestampCount > 0)
        {
            var detail = input.TimestampRegressionDetail is { } first
                ? $"{input.NonMonotonicTimestampCount} non-monotonic decode timestamp(s); first: {first}."
                : $"{input.NonMonotonicTimestampCount} non-monotonic decode timestamp(s).";
            return Fail("Timestamp integrity", detail);
        }

        return Pass("Timestamp integrity", "Decode timestamps increase monotonically across the output.");
    }

    private static VerificationCheck TailComplete(VerificationInput input)
    {
        // The output's last video frame should reach the source runtime. A material
        // shortfall means the encode was cut off and the final GOP is partial or missing —
        // dangerous because the output's own container header may still claim the full
        // length, so the duration gate (which compares headers) can pass on a truncated file.
        // Tolerances are generous enough to absorb last-frame and B-frame reorder slack:
        // only a shortfall that is both over a second and over 2% of the runtime fails.
        const double absoluteFloorSeconds = 1.0;
        const double tolerancePercent = 2.0;

        var original = OriginalVideoSpanSeconds(input)!.Value;
        var lastPresentation = Math.Max(
            0,
            input.OutputLastPresentationSeconds!.Value - (input.OutputVideoStartSeconds ?? 0));
        var shortfall = original - lastPresentation;
        var shortfallPercent = shortfall / original * 100.0;
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Output video spans {0:0.###}s against the source video's {1:0.###}s ({2:0.##}% short, tolerance {3:0.##}%).",
            lastPresentation, original, Math.Max(shortfallPercent, 0), tolerancePercent);

        return shortfall > absoluteFloorSeconds && shortfallPercent > tolerancePercent
            ? Fail("Tail integrity", $"{detail} The final GOP looks truncated.")
            : Pass("Tail integrity", detail);
    }

    private static VerificationCheck SourceVideoTimelineComplete(VerificationInput input)
    {
        if (input.SourceTimelineIndeterminate)
        {
            var sourceSpan = OriginalVideoSpanSeconds(input) ?? 0;
            var audioSpan = Math.Max(0, input.OriginalAudioLastPresentationSeconds!.Value
                - (input.OriginalAudioStartSeconds ?? 0));
            var outputSpan = Math.Max(0, input.OutputLastPresentationSeconds!.Value
                - (input.OutputVideoStartSeconds ?? 0));
            return Fail(SourceVideoTimelineCheckName,
                string.Format(CultureInfo.InvariantCulture,
                    "The source packet scan spans {0:0.###}s, but primary audio spans {1:0.###}s "
                    + "and encoded video spans {2:0.###}s. The source timeline measurement is "
                    + "indeterminate; the original is retained until its picture timeline can be verified.",
                    sourceSpan, audioSpan, outputSpan));
        }

        const double absoluteFloorSeconds = 1.0;
        const double tolerancePercent = 2.0;

        var videoDuration = OriginalVideoSpanSeconds(input)!.Value;
        var audioDuration = Math.Max(
            0,
            input.OriginalAudioLastPresentationSeconds!.Value - (input.OriginalAudioStartSeconds ?? 0));
        var shortfall = audioDuration - videoDuration;
        var shortfallPercent = audioDuration > 0 ? shortfall / audioDuration * 100.0 : 0;
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "The source video spans {0:0.###}s while its primary audio spans {1:0.###}s ({2:0.##}% short, tolerance {3:0.##}%).",
            videoDuration,
            audioDuration,
            Math.Max(shortfallPercent, 0),
            tolerancePercent);

        return shortfall > absoluteFloorSeconds && shortfallPercent > tolerancePercent
            ? Fail(SourceVideoTimelineCheckName, $"{detail} The source appears corrupt or has a materially incomplete picture stream.")
            : Pass(SourceVideoTimelineCheckName, detail);
    }

    private static double? OriginalVideoSpanSeconds(VerificationInput input) =>
        input.OriginalTimestampsMeasured && input.OriginalLastPresentationSeconds is { } last
            ? Math.Max(0, last - (input.OriginalVideoStartSeconds ?? 0))
            : input.OriginalDurationSeconds;

    private static VerificationCheck AvSync(VerificationInput input)
    {
        // A source can legitimately carry an inherent A/V start offset (audio priming, an
        // authored audio delay, container quirks) that plays fine. Faithfully preserving that
        // offset is not a desync — so when the original's start times are known, judge the
        // *change* the transcode made to the A/V relationship, not the output's absolute offset.
        const double toleranceSeconds = 0.5;
        var outputOffset = input.OutputVideoStartSeconds!.Value - input.OutputAudioStartSeconds!.Value;

        if (input.OriginalVideoStartSeconds is { } originalVideo
            && input.OriginalAudioStartSeconds is { } originalAudio)
        {
            var originalOffset = originalVideo - originalAudio;
            var drift = Math.Abs(outputOffset - originalOffset);
            var detail = string.Format(
                CultureInfo.InvariantCulture,
                "Original A/V start offset {0:0.###}s, output {1:0.###}s ({2:0.###}s change, tolerance {3:0.###}s).",
                originalOffset, outputOffset, drift, toleranceSeconds);

            return drift <= toleranceSeconds
                ? Pass("A/V sync", detail)
                : Fail("A/V sync", detail);
        }

        // Original start times unknown: fall back to the output's absolute A/V start divergence.
        var absoluteDrift = Math.Abs(outputOffset);
        var absoluteDetail = string.Format(
            CultureInfo.InvariantCulture,
            "Video starts at {0:0.###}s, audio at {1:0.###}s ({2:0.###}s offset, tolerance {3:0.###}s).",
            input.OutputVideoStartSeconds.Value, input.OutputAudioStartSeconds.Value, absoluteDrift, toleranceSeconds);

        return absoluteDrift <= toleranceSeconds
            ? Pass("A/V sync", absoluteDetail)
            : Fail("A/V sync", absoluteDetail);
    }

    private static VerificationCheck AudioFidelity(VerificationInput input)
    {
        if (input.OutputMaxAudioChannels < input.OriginalMaxAudioChannels)
        {
            // An operator-requested downmix (e.g. 5.1 -> 2.0) is an intentional reduction, not a
            // silent loss, so it passes — provided the output still carries audio.
            if (input.AudioDownmixed && input.OutputMaxAudioChannels > 0)
            {
                return Pass("Audio fidelity",
                    $"Audio intentionally downmixed from {input.OriginalMaxAudioChannels} to {input.OutputMaxAudioChannels} channels.");
            }

            return Fail("Audio fidelity",
                $"Audio was downmixed from {input.OriginalMaxAudioChannels} to {input.OutputMaxAudioChannels} channels.");
        }

        // An audio re-encode intentionally normalises the sample rate (e.g. Opus is always
        // 48 kHz), so a sample-rate change is expected and not a fidelity loss. This holds for
        // an audio-only job and for a video job that opted into re-encoding its audio; only a
        // copied audio track (the default for video) must keep the original rate.
        var audioReencoded = input.Kind == MediaKind.Audio || input.AudioReencoded;
        if (!audioReencoded
            && input.OriginalMaxAudioSampleRate > 0
            && input.OutputMaxAudioSampleRate > 0
            && input.OutputMaxAudioSampleRate < input.OriginalMaxAudioSampleRate)
        {
            return Fail("Audio fidelity",
                $"Audio sample rate dropped from {input.OriginalMaxAudioSampleRate} to {input.OutputMaxAudioSampleRate} Hz.");
        }

        return Pass("Audio fidelity",
            $"Channel layout ({input.OutputMaxAudioChannels} ch) retained.");
    }

    private static VerificationCheck HdrPreserved(VerificationInput input)
    {
        if (input.HdrConvertedToSdr)
        {
            return Pass("HDR signal", "Original is HDR and was intentionally tone-mapped to SDR.");
        }

        return input.OutputIsHdr
            ? Pass("HDR signal", "HDR signal preserved in the output.")
            : Fail("HDR signal", "Original is HDR but the output lost its HDR signal.");
    }

    private static VerificationCheck DolbyVisionKept(VerificationInput input)
    {
        if (input.OriginalDolbyVision is not { } source)
        {
            return Fail(DolbyVisionKeptCheckName,
                "The original is recorded as Dolby Vision, but no Dolby Vision configuration could be read from it, so the output cannot be shown to keep it. If the file was replaced since the last scan, scan the library again.");
        }

        if (input.OutputDolbyVision is not { } output)
        {
            return Fail(DolbyVisionKeptCheckName,
                $"The original is Dolby Vision ({source.Describe()}), but the output has no Dolby Vision configuration, so players would not show it as Dolby Vision.");
        }

        return source == output
            ? Pass(DolbyVisionKeptCheckName, $"The output keeps the original's Dolby Vision configuration ({source.Describe()}).")
            : Fail(DolbyVisionKeptCheckName,
                $"The output's Dolby Vision configuration ({output.Describe()}) differs from the original's ({source.Describe()}).");
    }

    private static VerificationCheck PerceptualQuality(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: if the gate is on but quality could not be measured, we cannot
        // prove the output is good enough, so replacement must not proceed.
        if (!input.QualityMeasured || input.QualityScores is null)
        {
            return Fail("Perceptual quality (VMAF)", $"Quality could not be measured: {Describe(input.QualityError)}");
        }

        var scores = input.QualityScores;
        if (scores.VmafHarmonicMean is null || scores.VmafMin is null)
        {
            return Fail("Perceptual quality (VMAF)", "VMAF aggregates were missing from the measurement.");
        }

        var detail = DescribeScores(scores, policy);
        // Netflix documents percentile pooling as a way to stop easy content hiding difficult
        // frames. New measurements carry a fifth percentile; older persisted reports fall back to
        // their minimum so they remain conservative and readable after upgrade.
        return VmafSoftwareConfirmation.MeetsGate(scores, policy)
            ? Pass("Perceptual quality (VMAF)", detail)
            : Fail("Perceptual quality (VMAF)", $"{detail} Below the quality gate.");
    }

    private static VerificationCheck ImageQuality(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: if the gate is on but SSIM could not be measured, we cannot prove the
        // re-encoded still is faithful, so replacement must not proceed.
        if (!input.ImageQualityMeasured || input.ImageSsim is not { } ssim)
        {
            return Fail("Image quality (SSIM)", $"SSIM could not be measured: {Describe(input.ImageQualityError)}");
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "SSIM {0:0.#####} (gate {1:0.#####}).",
            ssim, policy.MinimumImageSsim);

        return ssim >= policy.MinimumImageSsim
            ? Pass("Image quality (SSIM)", detail)
            : Fail("Image quality (SSIM)", $"{detail} Below the quality gate.");
    }

    private static VerificationCheck ImageMetadataPreserved(VerificationInput input)
    {
        const string name = "Image metadata (EXIF/ICC)";

        // Fail closed: an enabled gate that could not read the metadata cannot prove retention.
        if (!input.ImageMetadataMeasured)
        {
            return Fail(name, $"Image metadata could not be read: {Describe(input.ImageMetadataError)}");
        }

        var lost = new List<string>();
        if (input.OriginalHasIccProfile && !input.OutputHasIccProfile)
        {
            lost.Add("ICC colour profile");
        }
        if (input.OriginalHasExif && !input.OutputHasExif)
        {
            lost.Add("EXIF metadata");
        }

        if (lost.Count > 0)
        {
            return Fail(name, $"The re-encode dropped the original's {string.Join(" and ", lost)}.");
        }

        var present = new List<string>();
        if (input.OriginalHasIccProfile)
        {
            present.Add("ICC profile");
        }
        if (input.OriginalHasExif)
        {
            present.Add("EXIF");
        }

        var detail = present.Count > 0
            ? $"Retained the original's {string.Join(" and ", present)}."
            : "The original carried no ICC profile or EXIF to preserve.";
        return Pass(name, detail);
    }

    private static string DescribeScores(QualityScores scores, VerificationPolicy policy)
    {
        var parts = new List<string>
        {
            string.Format(
                CultureInfo.InvariantCulture,
                "VMAF harmonic mean {0:0.##} (gate {1:0.##}), fifth percentile {2:0.##} (gate {3:0.##}), lowest frame {4:0.##} (catastrophic floor {5:0.##})",
                scores.VmafHarmonicMean, policy.MinimumVmafHarmonicMean,
                scores.VmafFifthPercentile ?? scores.VmafMin, policy.MinimumVmafMin,
                scores.VmafMin, policy.MinimumVmafCatastrophicMin)
        };

        if (scores.VmafMean is { } mean)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "mean {0:0.##}", mean));
        }
        if (scores.PsnrYMean is { } psnr)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "PSNR-Y {0:0.##} dB", psnr));
        }
        if (scores.SsimMean is { } ssim)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "SSIM {0:0.####}", ssim));
        }
        if (!string.IsNullOrWhiteSpace(scores.ModelVersion))
        {
            parts.Add($"model {scores.ModelVersion}");
        }
        if (!string.IsNullOrWhiteSpace(scores.Preprocessing))
        {
            parts.Add(scores.Preprocessing);
        }

        return string.Join("; ", parts) + ".";
    }

    private static VerificationCheck DecodeHealth(VerificationInput input) =>
        input.DecodeSucceeded
            ? Pass("Decode health", "Output decoded fully with no FFmpeg errors.")
            : Fail("Decode health", $"FFmpeg reported {input.DecodeErrorCount} decode error(s): {Describe(input.DecodeError)}");

    private static VerificationCheck OutputReadable(VerificationInput input) =>
        input.OutputProbeSucceeded
            ? Pass("Output readable", "ffprobe read the output container and streams.")
            : Fail("Output readable", $"ffprobe could not read the output: {Describe(input.OutputProbeError)}");

    private static VerificationCheck PicturePresent(VerificationInput input) =>
        !string.IsNullOrWhiteSpace(input.OutputVideoCodec)
            ? Pass("Picture", $"Output has a picture stream ({input.OutputVideoCodec}).")
            : Fail("Picture", "Output has no picture stream.");

    private static VerificationCheck DimensionsRetained(VerificationInput input)
    {
        if (input.OriginalWidth is not { } ow || input.OriginalHeight is not { } oh)
        {
            return Pass("Dimensions", "Original dimensions unknown; not compared.");
        }

        if (input.OutputWidth is not { } nw || input.OutputHeight is not { } nh)
        {
            // The original had readable dimensions but the output does not — a real loss.
            return Fail("Dimensions", $"Original {ow}x{oh}, output dimensions could not be read.");
        }

        var detail = $"Original {ow}x{oh}, output {nw}x{nh}.";

        // An operator-requested downscale is an intentional reduction, not corruption. It must
        // still shrink rather than enlarge, and keep the aspect ratio (so the picture isn't
        // stretched), but a smaller output is the expected, passing outcome — mirroring how an
        // intentional audio downmix is treated.
        if (input.ImageDownscaleRequested)
        {
            if (nw > ow || nh > oh)
            {
                return Fail("Dimensions", $"{detail} A downscale must not enlarge the image.");
            }

            // Compare aspect ratios with a small tolerance to absorb even-pixel rounding.
            var originalAspect = ow / (double)oh;
            var outputAspect = nw / (double)nh;
            const double aspectTolerance = 0.02;
            return Math.Abs(originalAspect - outputAspect) <= originalAspect * aspectTolerance
                ? Pass("Dimensions", $"{detail} Intentionally downscaled.")
                : Fail("Dimensions", $"{detail} The aspect ratio changed during downscale.");
        }

        // No downscale requested: the picture must keep (at least) its dimensions; any shrink is
        // a degenerate/corrupt encode.
        return nw >= ow && nh >= oh
            ? Pass("Dimensions", detail)
            : Fail("Dimensions", $"{detail} The image was unexpectedly downscaled.");
    }

    private static VerificationCheck VideoStreamPresent(VerificationInput input) =>
        !string.IsNullOrWhiteSpace(input.OutputVideoCodec)
            ? Pass("Video stream", $"Output has a video stream ({input.OutputVideoCodec}).")
            : Fail("Video stream", "Output has no video stream.");

    private static VerificationCheck PixelShapePreserved(VerificationInput input)
    {
        var original = input.OriginalPixelAspect ?? new SampleAspectRatio(1, 1);
        var output = input.OutputPixelAspect ?? new SampleAspectRatio(1, 1);
        return SampleAspectRatio.SameShape(original, output)
            ? Pass("Picture shape", $"Source and output pixels have the same shape ({original}).")
            : Fail("Picture shape", $"Source pixels have shape {original}; output pixels have {output}. "
                + "The output would display squashed or stretched.");
    }

    private static VerificationCheck VideoArtworkPreserved(VerificationInput input)
    {
        return input.OutputAttachedPictureCount < input.OriginalAttachedPictureCount
            ? Fail(EmbeddedArtworkCheckName, $"The original has {input.OriginalAttachedPictureCount} embedded cover picture(s); the output has "
                + $"{input.OutputAttachedPictureCount}. The cover art was dropped or is no longer marked as cover art.")
            : Pass(EmbeddedArtworkCheckName, $"Kept {input.OriginalAttachedPictureCount} embedded picture(s).");
    }

    private static VerificationCheck VideoStructurePreserved(VerificationInput input)
    {
        const string name = "Video structure";
        var requiredCodec = input.VideoReencoded ? input.ExpectedVideoCodec : input.OriginalVideoCodec;
        if (string.IsNullOrWhiteSpace(requiredCodec)
            || !string.Equals(requiredCodec, input.OutputVideoCodec, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(name,
                $"Expected codec {Describe(requiredCodec)}, output codec {Describe(input.OutputVideoCodec)}.");
        }

        if (input.OriginalWidth is not { } originalWidth || input.OriginalHeight is not { } originalHeight
            || input.OutputWidth is not { } outputWidth || input.OutputHeight is not { } outputHeight)
        {
            return Fail(name, "Source/output video dimensions could not be compared.");
        }

        // A re-encode that was told to scale is judged against what it was told, not against the
        // source. An output at the source size when a downscale was intended is the interesting
        // failure: the file is sound, but it is not the job that was asked for, and it must not
        // replace the original under the belief that it is smaller. A copied stream never has an
        // intent to honour — it cannot be scaled — so it falls through to the source-size rule.
        if (input.VideoReencoded
            && input.ExpectedWidth is { } expectedWidth
            && input.ExpectedHeight is { } expectedHeight)
        {
            if (outputWidth != expectedWidth || outputHeight != expectedHeight)
            {
                return Fail(name,
                    $"Video resolution is {outputWidth}x{outputHeight}; the encode intended {expectedWidth}x{expectedHeight} (source {originalWidth}x{originalHeight}).");
            }
        }
        else if (originalWidth != outputWidth || originalHeight != outputHeight)
        {
            return Fail(name,
                $"Video resolution changed from {originalWidth}x{originalHeight} to {outputWidth}x{outputHeight} without a resize policy.");
        }

        // Likewise a re-encode told to decimate is held to the rate it was told. An output at the
        // source rate means the fps filter was dropped somewhere; the file is sound but it is not
        // the job that was asked for. Probed rates carry rounding, so the comparison allows half a
        // percent — far under the factor of two any real miss would show.
        if (input.VideoReencoded && input.ExpectedFrameRate is { } expectedFrameRate)
        {
            if (input.OutputFrameRate is not { } outputFrameRate || outputFrameRate <= 0)
            {
                return Fail(name,
                    $"Video frame rate could not be read; the encode intended {expectedFrameRate:0.###} fps.");
            }

            if (Math.Abs(outputFrameRate - expectedFrameRate) / expectedFrameRate > FrameRateTolerance)
            {
                return Fail(name,
                    $"Video frame rate is {outputFrameRate:0.###} fps; the encode intended {expectedFrameRate:0.###} fps.");
            }
        }

        var originalFormat = PixelFormatInfo.Parse(input.OriginalPixelFormat, input.OriginalBitsPerRawSample);
        var outputFormat = PixelFormatInfo.Parse(input.OutputPixelFormat, input.OutputBitsPerRawSample);
        if (originalFormat is null || outputFormat is null)
        {
            return Fail(name,
                $"Pixel format could not be compared (source {Describe(input.OriginalPixelFormat)}, output {Describe(input.OutputPixelFormat)}).");
        }

        if (outputFormat.Value.BitDepth < originalFormat.Value.BitDepth)
        {
            return Fail(name,
                $"Video bit depth dropped from {originalFormat.Value.BitDepth}-bit to {outputFormat.Value.BitDepth}-bit.");
        }

        if (outputFormat.Value.ChromaRank < originalFormat.Value.ChromaRank)
        {
            return Fail(name,
                $"Chroma sampling was reduced ({input.OriginalPixelFormat} → {input.OutputPixelFormat}).");
        }

        if (string.IsNullOrWhiteSpace(input.OutputVideoProfile))
        {
            return Fail(name, "Output video profile was not reported.");
        }

        if (!input.VideoReencoded
            && !string.Equals(input.OriginalVideoProfile, input.OutputVideoProfile, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(name,
                $"Remux changed the video profile from {Describe(input.OriginalVideoProfile)} to {input.OutputVideoProfile}.");
        }

        if (outputFormat.Value.BitDepth > 8
            && !input.OutputVideoProfile.Contains("10", StringComparison.OrdinalIgnoreCase)
            && !input.OutputVideoProfile.Contains("12", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(input.OutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(name,
                $"Output profile '{input.OutputVideoProfile}' does not describe its {outputFormat.Value.BitDepth}-bit signal.");
        }

        return Pass(name,
            $"{outputWidth}x{outputHeight}, {input.OutputPixelFormat}, profile {input.OutputVideoProfile}, codec {input.OutputVideoCodec}.");
    }

    private static VerificationCheck DurationWithinTolerance(VerificationInput input, VerificationPolicy policy)
    {
        if (input.OriginalDurationSeconds is not { } original || original <= 0)
        {
            return Fail("Duration", "Original duration is unknown, so the output cannot be compared.");
        }

        if (input.OutputDurationSeconds is not { } output || output <= 0)
        {
            return Fail("Duration", "Output duration is unknown.");
        }

        // A trimmed file is meant to be shorter by exactly the silence removed.
        var expected = input.SilenceTrim?.KeptSeconds ?? original;
        var driftSeconds = Math.Abs(expected - output);
        var driftPercent = driftSeconds / expected * 100.0;
        var detail = input.SilenceTrim is { } trim
            ? string.Format(
                CultureInfo.InvariantCulture,
                "Expected {0:0.###}s after trimming {1:0.###}s of silence from the original {2:0.###}s; output {3:0.###}s ({4:0.##}% drift, tolerance {5:0.##}%).",
                expected, original - expected, original, output, driftPercent, policy.DurationTolerancePercent)
            : string.Format(
                CultureInfo.InvariantCulture,
                "Original {0:0.###}s, output {1:0.###}s ({2:0.##}% drift, tolerance {3:0.##}%).",
                original, output, driftPercent, policy.DurationTolerancePercent);

        return driftPercent <= policy.DurationTolerancePercent
            ? Pass("Duration", detail)
            : Fail("Duration", detail);
    }

    /// <summary>Within this of the planned length, the cut is where it was planned. Allows for codec padding.</summary>
    internal const double TrimLengthToleranceSeconds = 0.15;

    /// <summary>A trimmed edge keeps its padding, give or take this much; any further and the cut moved.</summary>
    internal const double TrimEdgeToleranceSeconds = 0.1;

    /// <summary>
    /// The percentage duration check alone cannot prove a trim: one percent of a four-minute track is
    /// more than two seconds. The exact length pins how much was cut. Where it fell is pinned at each
    /// trimmed edge from both sides: no more silence than the padding at the trim's own level, so the
    /// cut did not stop short, and no less at a lenient level, so it did not run into the music.
    /// </summary>
    private static VerificationCheck SilenceTrimmedAsPlanned(VerificationInput input, Optimisarr.Core.Queue.AudioTrimPlan trim)
    {
        const string name = "Silence trimmed";
        if (input.OutputDurationSeconds is not { } output || output <= 0)
            return Fail(name, "Output duration is unknown, so the trim cannot be checked.");
        if (input.OutputEdgeSilence is not { } edges)
            return Fail(name, "The output's silence could not be measured, so the trim cannot be checked.");

        var padding = Optimisarr.Core.Queue.SilenceTrimPlanner.PaddingSeconds;
        var most = padding + TrimEdgeToleranceSeconds;
        var least = padding - TrimEdgeToleranceSeconds;
        var detail = string.Format(CultureInfo.InvariantCulture,
            "Planned {0:0.###}s kept, output {1:0.###}s; it starts with {2:0.##}s and ends with {3:0.##}s of silence.",
            trim.KeptSeconds, output, edges.Strict.Leading, edges.Strict.Trailing);
        var lengthOff = Math.Abs(output - trim.KeptSeconds);
        if (lengthOff > TrimLengthToleranceSeconds)
            return Fail(name, detail + string.Format(CultureInfo.InvariantCulture,
                " The length is {0:0.###}s off the plan (allowed {1:0.##}s).", lengthOff, TrimLengthToleranceSeconds));
        if (trim.TrimsStart && edges.Strict.Leading > most || trim.TrimsEnd && edges.Strict.Trailing > most)
            return Fail(name, detail + string.Format(CultureInfo.InvariantCulture,
                " A trimmed edge kept more than {0:0.##}s of silence, so the cut stopped short of where it was planned.", most));
        if (trim.TrimsStart && edges.Lenient.Leading < least || trim.TrimsEnd && edges.Lenient.Trailing < least)
            return Fail(name, detail + string.Format(CultureInfo.InvariantCulture,
                " A trimmed edge kept less than {0:0.##}s of silence, so the cut reached into the music.", least));
        return Pass(name, detail);
    }

    /// <summary>
    /// A trim-only file changes nothing but its length. A lossless one must decode to the source's
    /// exact samples over the kept window; a copied one must hold the source's own encoded packets,
    /// every one, in order, with none changed or missing. Unmeasured evidence fails.
    /// </summary>
    private static VerificationCheck KeptAudioUnchanged(KeptAudioEvidence kept)
    {
        const string name = KeptAudioCheckName;
        if (kept.Method == Optimisarr.Core.Queue.AudioTrimOnlyMethod.LosslessReencode)
        {
            if (kept.SourceDecodedDigest is null || kept.OutputDecodedDigest is null)
                return Fail(name, "The decoded audio could not be compared with the source's.");
            return string.Equals(kept.SourceDecodedDigest, kept.OutputDecodedDigest, StringComparison.OrdinalIgnoreCase)
                ? Pass(name, "Every sample between the cuts is identical to the source's.")
                : Fail(name, "The decoded audio between the cuts differs from the source's.");
        }

        if (kept.SourcePacketDigests is null || kept.OutputPacketDigests is null)
            return Fail(name, "The audio frames could not be compared with the source's.");
        return PacketRun.IsContiguousRunOf(kept.OutputPacketDigests, kept.SourcePacketDigests)
            ? Pass(name, string.Format(CultureInfo.InvariantCulture,
                "All {0} audio frames are unchanged copies of consecutive frames of the source's {1}.",
                kept.OutputPacketDigests.Count, kept.SourcePacketDigests.Count))
            : Fail(name, "The audio frames are not an unbroken, unchanged run of the source's frames.");
    }

    public const string KeptAudioCheckName = "Kept audio unchanged";
    public const string TagsKeptCheckName = "Tags kept unchanged";

    /// <summary>
    /// A trim-only file keeps every tag the source has, frame for frame and value for value, apart
    /// from those written again for the new length. Tags that could not be read fail.
    /// </summary>
    private static VerificationCheck TagsKeptUnchanged(TagsKeptEvidence evidence)
    {
        if (evidence.Source?.Tags is not { } source)
            return Fail(TagsKeptCheckName, $"The original's tags could not be read: {evidence.Source?.Problem ?? "not measured"}.");
        if (evidence.Output?.Tags is not { } output)
            return Fail(TagsKeptCheckName, $"The new file's tags could not be read: {evidence.Output?.Problem ?? "not measured"}.");
        var comparison = TrimOnlyTags.Compare(source, output);
        return comparison.Kept ? Pass(TagsKeptCheckName, comparison.Detail) : Fail(TagsKeptCheckName, comparison.Detail);
    }

    private static VerificationCheck AudioRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalAudioTrackCount} audio track(s), output {input.OutputAudioTrackCount}.";
        // Tracks the kept-languages rule removed are intentional, so expect exactly that many
        // fewer — but a source that had audio must never verify with none at all, even if the
        // planned removal claims otherwise (the selection logic forbids removing every track).
        if (input.AudioTracksRemoved > 0)
        {
            var expected = input.OriginalAudioTrackCount - input.AudioTracksRemoved;
            if (input.OriginalAudioTrackCount > 0)
            {
                expected = Math.Max(expected, 1);
            }

            detail += $" {input.AudioTracksRemoved} track(s) intentionally removed by the kept-languages rule.";
            return input.OutputAudioTrackCount == expected
                ? Pass("Audio tracks", detail)
                : Fail(
                    "Audio tracks",
                    $"{detail} Expected exactly {expected} track(s) after the planned removal.");
        }

        if (!policy.RequireAudioRetained)
        {
            return Pass("Audio tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputAudioTrackCount >= input.OriginalAudioTrackCount
            ? Pass("Audio tracks", detail)
            : Fail("Audio tracks", $"{detail} Audio tracks were lost.");
    }

    private static VerificationCheck SubtitlesRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalSubtitleTrackCount} subtitle track(s), output {input.OutputSubtitleTrackCount}.";

        if (input.SubtitleTracksRemoved > 0)
        {
            // The plan is exact: an encode that drops a stream beyond the plan must fail even
            // when the policy does not require retention, and unlike audio a zero-subtitle
            // output is legitimate (no minimum-one floor — an all-foreign set goes entirely).
            var expected = Math.Max(input.OriginalSubtitleTrackCount - input.SubtitleTracksRemoved, 0);
            detail += $" {input.SubtitleTracksRemoved} track(s) intentionally removed by the kept-languages rule.";
            return input.OutputSubtitleTrackCount == expected
                ? Pass("Subtitle tracks", detail)
                : Fail("Subtitle tracks", $"{detail} Expected exactly {expected} track(s) after the planned removal.");
        }

        if (!policy.RequireSubtitlesRetained)
        {
            return Pass("Subtitle tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputSubtitleTrackCount >= input.OriginalSubtitleTrackCount
            ? Pass("Subtitle tracks", detail)
            : Fail("Subtitle tracks", $"{detail} Subtitle tracks were lost.");
    }

    private static VerificationCheck LanguagesRetained(
        string name,
        IReadOnlyList<string?> expected,
        IReadOnlyList<string?>? output)
    {
        if (output is null || output.Count != expected.Count)
        {
            return Fail(
                name,
                $"Expected language evidence for {expected.Count} retained track(s), output reported {output?.Count ?? 0}.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            // An unknown source tag was deliberately preserved, but its identity cannot be
            // compared honestly. The exact stream-count gate still proves that no extra stream
            // disappeared. Known identifiers, however, must survive in positional order.
            if (!TrackLanguages.TryCanonicaliseKnown(expected[index], out var expectedLanguage))
            {
                continue;
            }

            if (!TrackLanguages.TryCanonicaliseKnown(output[index], out var outputLanguage)
                || !string.Equals(expectedLanguage, outputLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    name,
                    $"Retained track {index + 1} changed language from {expectedLanguage} to {output[index] ?? "unknown"}.");
            }
        }

        return Pass(name, $"Verified the language identity of {expected.Count} retained track(s).");
    }

    // ffprobe's format_name is a demuxer name shared by every extension it serves, so a
    // straight comparison holds exactly when the container type is genuinely the same.
    private static VerificationCheck ContainerUnchanged(VerificationInput input)
    {
        var detail = $"Original \"{input.OriginalContainer}\", output \"{input.OutputContainer}\".";
        return input.OutputContainer is not null
            && string.Equals(input.OriginalContainer, input.OutputContainer, StringComparison.OrdinalIgnoreCase)
            ? Pass("Container unchanged", detail)
            : Fail("Container unchanged", $"{detail} The container type must not change under this profile.");
    }

    private static VerificationCheck AudioCodecsPreserved(VerificationInput input)
    {
        const string name = "Audio codecs unchanged";
        if (input.ExpectedAudioCodecs is null || input.OutputAudioCodecs is null)
        {
            return Fail(name, "Source/output audio codec evidence could not be compared.");
        }

        if (input.ExpectedAudioCodecs.Count != input.OutputAudioCodecs.Count)
        {
            return Fail(
                name,
                $"Expected {input.ExpectedAudioCodecs.Count} retained audio codec(s), output reported {input.OutputAudioCodecs.Count}.");
        }

        for (var index = 0; index < input.ExpectedAudioCodecs.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(input.ExpectedAudioCodecs[index])
                || !string.Equals(
                    input.ExpectedAudioCodecs[index],
                    input.OutputAudioCodecs[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    name,
                    $"Retained audio track {index + 1} changed codec from {input.ExpectedAudioCodecs[index] ?? "unknown"} to {input.OutputAudioCodecs[index] ?? "unknown"}.");
            }
        }

        return Pass(name, $"All {input.ExpectedAudioCodecs.Count} retained audio codec(s) are unchanged.");
    }

    private static VerificationCheck SizeReduced(VerificationInput input, VerificationPolicy policy)
    {
        if (input.OutputSizeBytes <= 0)
        {
            return Fail("Size saving", "Output file is empty or missing.");
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:n0} bytes, output {1:n0} bytes ({2:+0.#;-0.#}% change).",
            input.OriginalSizeBytes,
            input.OutputSizeBytes,
            PercentChange(input.OriginalSizeBytes, input.OutputSizeBytes));

        if (!policy.RequireSizeReduction)
        {
            return Pass("Size saving", $"{detail} Reduction not required by policy.");
        }

        var minimum = input.Kind == MediaKind.Video && input.VideoReencoded
            ? policy.MinimumSizeSavingPercent
            : null;
        var maximumBytes = SizeBudget.MaxCandidateBytes(
            input.OriginalSizeBytes, requireReduction: true, disposable: false,
            minimumSavingPercent: minimum);
        if (maximumBytes is null)
        {
            return Fail("Size saving", $"{detail} Original size is unavailable.");
        }

        return input.OutputSizeBytes <= maximumBytes.Value
            ? Pass("Size saving", detail)
            : Fail("Size saving", minimum is > 0
                ? $"{detail} At least {minimum:0.##}% saving is required for this video re-encode."
                : $"{detail} Output is not smaller than the original.");
    }

    private static double PercentChange(long original, long output) =>
        original > 0 ? (output - original) / (double)original * 100.0 : 0;

    private static VerificationCheck CompressionCeiling(VerificationInput input, double maximumSavingPercent)
    {
        var minimumBytes = SizeBudget.MinCandidateBytes(
            input.OriginalSizeBytes, requireReduction: true, disposable: false,
            maximumSavingPercent: maximumSavingPercent);
        return minimumBytes is { } minimum && input.OutputSizeBytes >= minimum
            ? Pass("Compression ceiling", $"Output remains within the {maximumSavingPercent:0.##}% maximum saving.")
            : Fail("Compression ceiling",
                $"Output is smaller than the {maximumSavingPercent:0.##}% maximum saving allows.");
    }

    private static string Describe(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "no detail available" : error;

    private static VerificationCheck Pass(string name, string detail) =>
        new(name, CheckOutcome.Passed, detail);

    private static VerificationCheck Fail(string name, string detail) =>
        new(name, CheckOutcome.Failed, detail);
}
