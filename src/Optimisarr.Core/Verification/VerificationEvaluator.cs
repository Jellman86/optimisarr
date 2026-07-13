using System.Globalization;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure verification: turns a gathered <see cref="VerificationInput"/> into a
/// <see cref="VerificationReport"/>. No I/O, no FFmpeg — every check is a
/// deterministic comparison so it can be unit tested without media on disk.
/// </summary>
public static class VerificationEvaluator
{
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
            checks.Add(AudioRetained(input, policy));
            checks.Add(SubtitlesRetained(input, policy));
        }

        // Music metadata and embedded artwork are part of the media, not decoration. Audio-only
        // jobs must prove both survived before their source can be replaced.
        if (input.Kind == MediaKind.Audio)
        {
            checks.Add(AudioMetadataPreserved(input));
        }

        checks.Add(SizeReduced(input, policy));

        // A still is verified as an image: it must contain a picture and keep its dimensions.
        // No downscaling is performed yet, so any shrink is an unintended/degenerate encode.
        if (isImage)
        {
            checks.Add(PicturePresent(input));
            checks.Add(DimensionsRetained(input));

            // The structural-quality (SSIM) gate is the still-image counterpart of VMAF and
            // only contributes when the user has opted in; otherwise the report is unchanged.
            if (policy.ImageQualityGateEnabled)
            {
                checks.Add(ImageQuality(input, policy));
            }

            // The metadata gate fails an image whose re-encode silently dropped the source's
            // ICC colour profile or EXIF; opt-in, and only flags loss (never a gain).
            if (policy.ImageMetadataGateEnabled)
            {
                checks.Add(ImageMetadataPreserved(input));
            }
        }

        if (isVideo)
        {
            checks.Add(VideoStreamPresent(input));
        }

        // HDR preservation only matters when the original carries an HDR signal, so
        // SDR sources don't get a noisy not-applicable line.
        if (isVideo && input.OriginalIsHdr)
        {
            checks.Add(HdrPreserved(input));
        }

        // Audio fidelity (no silent downmix or sample-rate drop) is checked when audio
        // retention is required and the original's audio shape is known.
        if (policy.RequireAudioRetained && input.OriginalMaxAudioChannels > 0)
        {
            checks.Add(AudioFidelity(input));
        }

        // Colour metadata is only worth comparing when the original declared some.
        if (isVideo
            && (input.OriginalColorPrimaries is not null
                || input.OriginalColorTransfer is not null
                || input.OriginalColorSpace is not null))
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
        if (isVideo && input.TimestampsMeasured)
        {
            checks.Add(MonotonicTimestamps(input));
        }

        // A truncated/partial last GOP shows up as the output's video ending well before
        // the source runtime. It needs the source duration and the output's real last
        // presentation time, so it is checked only when both are known.
        if (isVideo
            && input.TimestampsMeasured
            && input.OutputLastPresentationSeconds is not null
            && input.OriginalDurationSeconds is > 0)
        {
            checks.Add(TailComplete(input));
        }

        // Remuxes copy the encoded frames unchanged, so VMAF is both redundant and expensive.
        // Non-video media use their applicable audio/image gates instead.
        if (policy.RequiresVmaf(input.Kind, input.VideoReencoded))
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

        return new VerificationReport(checks);
    }

    private static VerificationCheck AudioMetadataPreserved(VerificationInput input)
    {
        const string name = "Audio metadata and artwork";
        if (input.OutputAttachedPictureCount < input.OriginalAttachedPictureCount)
        {
            return Fail(name,
                $"Embedded artwork dropped from {input.OriginalAttachedPictureCount} to {input.OutputAttachedPictureCount} picture(s).");
        }

        var original = NormaliseAudioTags(input.OriginalFormatTags);
        var output = NormaliseAudioTags(input.OutputFormatTags);
        foreach (var (key, value) in original)
        {
            if (!output.TryGetValue(key, out var outputValue))
            {
                return Fail(name, $"Audio metadata tag '{key}' was lost.");
            }

            if (!string.Equals(value, outputValue, StringComparison.Ordinal))
            {
                return Fail(name, $"Audio metadata tag '{key}' changed during conversion.");
            }
        }

        return Pass(name,
            $"Retained {original.Count} source metadata tag(s) and {input.OriginalAttachedPictureCount} embedded picture(s).");
    }

    private static IReadOnlyDictionary<string, string> NormaliseAudioTags(
        IReadOnlyDictionary<string, string>? tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is null)
        {
            return result;
        }

        foreach (var (rawKey, rawValue) in tags)
        {
            var key = rawKey.Trim().ToLowerInvariant() switch
            {
                "albumartist" => "album_artist",
                "year" => "date",
                var other => other
            };

            // Technical container fields are regenerated legitimately and are not music tags.
            if (key is "encoder" or "vendor_id" or "major_brand" or "minor_version"
                or "compatible_brands" or "handler_name" or "duration" or "language"
                or "optimisarr")
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                result[key] = rawValue.Trim();
            }
        }

        return result;
    }

    private static VerificationCheck NoClippingIntroduced(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: an enabled gate that could not measure the true peak blocks replacement.
        if (!input.TruePeakMeasured || input.OriginalTruePeakDbtp is not { } original || input.OutputTruePeakDbtp is not { } output)
        {
            return Fail("Audio clipping (true peak)", $"True peak could not be measured: {Describe(input.TruePeakError)}");
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:0.#} dBTP, output {1:0.#} dBTP (ceiling {2:0.#} dBTP).",
            original, output, policy.MaxTruePeakDbtp);

        // Clipping is only "introduced" when the output rises above the ceiling while the
        // original stayed at or below it — an already-hot source isn't the re-encode's fault.
        // A small margin absorbs measurement noise so an unchanged level never trips the gate.
        const double measurementMarginDb = 0.1;
        var introducedClipping = output > policy.MaxTruePeakDbtp
            && output > original + measurementMarginDb;

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

            return unexpected.Count == 0
                ? Pass("Colour metadata", "Intentional HDR-to-SDR output is tagged as Rec.709.")
                : Fail("Colour metadata", $"Tone-mapped SDR metadata is invalid: {string.Join("; ", unexpected)}.");
        }

        // Only a definite change is a failure: the original and output both declare a
        // value and they differ (e.g. BT.709 re-tagged as BT.601). A dropped tag on the
        // output is treated as benign, since absence usually means "container default".
        var mismatches = new List<string>();
        AddMismatch(mismatches, "primaries", input.OriginalColorPrimaries, input.OutputColorPrimaries);
        AddMismatch(mismatches, "transfer", input.OriginalColorTransfer, input.OutputColorTransfer);
        AddMismatch(mismatches, "matrix", input.OriginalColorSpace, input.OutputColorSpace);

        return mismatches.Count == 0
            ? Pass("Colour metadata", "Colour primaries, transfer, and matrix preserved.")
            : Fail("Colour metadata", $"Colour metadata changed: {string.Join("; ", mismatches)}.");
    }

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

        var original = input.OriginalDurationSeconds!.Value;
        var lastPresentation = input.OutputLastPresentationSeconds!.Value;
        var shortfall = original - lastPresentation;
        var shortfallPercent = shortfall / original * 100.0;
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Output video ends at {0:0.###}s of the source's {1:0.###}s ({2:0.##}% short, tolerance {3:0.##}%).",
            lastPresentation, original, Math.Max(shortfallPercent, 0), tolerancePercent);

        return shortfall > absoluteFloorSeconds && shortfallPercent > tolerancePercent
            ? Fail("Tail integrity", $"{detail} The final GOP looks truncated.")
            : Pass("Tail integrity", detail);
    }

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

    private static VerificationCheck PerceptualQuality(VerificationInput input, VerificationPolicy policy)
    {
        // Fail closed: if the gate is on but quality could not be measured, we cannot
        // prove the output is good enough, so replacement must not proceed.
        if (!input.QualityMeasured || input.QualityScores is null)
        {
            return Fail("Perceptual quality (VMAF)", $"Quality could not be measured: {Describe(input.QualityError)}");
        }

        var scores = input.QualityScores;
        if (scores.VmafHarmonicMean is not { } harmonic || scores.VmafMin is not { } min)
        {
            return Fail("Perceptual quality (VMAF)", "VMAF aggregates were missing from the measurement.");
        }

        var detail = DescribeScores(scores, policy);
        return harmonic >= policy.MinimumVmafHarmonicMean && min >= policy.MinimumVmafMin
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
                "VMAF harmonic mean {0:0.##} (gate {1:0.##}), lowest frame {2:0.##} (gate {3:0.##})",
                scores.VmafHarmonicMean, policy.MinimumVmafHarmonicMean,
                scores.VmafMin, policy.MinimumVmafMin)
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

        var driftSeconds = Math.Abs(original - output);
        var driftPercent = driftSeconds / original * 100.0;
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "Original {0:0.###}s, output {1:0.###}s ({2:0.##}% drift, tolerance {3:0.##}%).",
            original, output, driftPercent, policy.DurationTolerancePercent);

        return driftPercent <= policy.DurationTolerancePercent
            ? Pass("Duration", detail)
            : Fail("Duration", detail);
    }

    private static VerificationCheck AudioRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalAudioTrackCount} audio track(s), output {input.OutputAudioTrackCount}.";
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
        if (!policy.RequireSubtitlesRetained)
        {
            return Pass("Subtitle tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputSubtitleTrackCount >= input.OriginalSubtitleTrackCount
            ? Pass("Subtitle tracks", detail)
            : Fail("Subtitle tracks", $"{detail} Subtitle tracks were lost.");
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

        return input.OutputSizeBytes < input.OriginalSizeBytes
            ? Pass("Size saving", detail)
            : Fail("Size saving", $"{detail} Output is not smaller than the original.");
    }

    private static double PercentChange(long original, long output) =>
        original > 0 ? (output - original) / (double)original * 100.0 : 0;

    private static string Describe(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "no detail available" : error;

    private static VerificationCheck Pass(string name, string detail) =>
        new(name, CheckOutcome.Passed, detail);

    private static VerificationCheck Fail(string name, string detail) =>
        new(name, CheckOutcome.Failed, detail);
}
