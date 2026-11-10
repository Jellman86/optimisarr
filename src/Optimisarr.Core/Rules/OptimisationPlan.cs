using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Rules;

/// <summary>What a re-encode does to the picture. A null size or cap means it is left as it is.</summary>
public sealed record PlannedPicture(
    string Codec,
    int? Crf,
    int? Width,
    int? Height,
    int? FrameRateCap,
    bool TonemapToSdr,
    bool Deinterlace,
    bool CropBlackBars,
    NoiseReduction NoiseReduction);

/// <summary>What happens to the sound. A null codec means every kept track is copied untouched.</summary>
public sealed record PlannedSound(string? Codec, int? BitrateKbps, bool DownmixToStereo);

/// <summary>One audio or subtitle track of the source, in stream order, and whether the new file keeps it.</summary>
public sealed record PlannedTrack(int Index, string? Language, bool Kept);

/// <summary>
/// The file that would be written. <see cref="MayBecomeMatroska"/> is true when an MP4 result can
/// still move to Matroska, which is only known once the job has read the file's subtitle and audio
/// formats.
/// </summary>
public sealed record PlannedFile(string Container, string FileName, bool Renamed, bool MayBecomeMatroska);

/// <summary>
/// What optimising one file would do, by its library's settings as they are now. Read-only: the
/// same resolver the queue uses, run over the facts the inventory already holds, with no FFmpeg.
/// </summary>
public sealed record OptimisationPlan(
    MediaKind Kind,
    string Action,
    PlannedPicture? Picture,
    PlannedSound? Sound,
    IReadOnlyList<PlannedTrack>? AudioTracks,
    IReadOnlyList<PlannedTrack>? SubtitleTracks,
    PlannedFile File);

public static class OptimisationPlanner
{
    public const string Encode = "Encode";
    public const string Remux = "Remux";
    public const string TrackCleanup = "TrackCleanup";
    public const string Audio = "Audio";
    public const string Image = "Image";

    /// <param name="crf">The library's own quality number, or its preset's when it has none.</param>
    public static OptimisationPlan Plan(MediaProperties media, RuleSettings rules, int? crf)
    {
        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.RelativePath,
            media.RelativePath,
            workRoot: string.Empty,
            media.IsHdr,
            crf,
            preset: null,
            media.Kind,
            sourceImageCodec: media.VideoCodec,
            sourceMaxAudioChannels: media.MaxAudioChannels,
            sourceAudioLanguages: media.AudioLanguages,
            sourceSubtitleLanguages: media.SubtitleLanguages,
            sourceWidth: media.Width,
            sourceHeight: media.Height);

        var fileName = Path.GetFileName(spec.OutputPath);
        var container = Path.GetExtension(fileName).TrimStart('.');
        var file = new PlannedFile(
            container,
            fileName,
            Renamed: !string.Equals(fileName, Path.GetFileName(media.RelativePath), StringComparison.Ordinal),
            // Only a video written as MP4 can still be moved: picture subtitles and some copied
            // audio formats do not fit in MP4, and the inventory does not record either.
            MayBecomeMatroska: media.Kind == MediaKind.Video
                && rules.TargetContainer is not null
                && TranscodeSpecResolver.IsMp4Container(container));

        if (media.Kind == MediaKind.Image)
        {
            return new OptimisationPlan(media.Kind, Image, null, null, null, null, file);
        }

        if (media.Kind == MediaKind.Audio)
        {
            return new OptimisationPlan(
                media.Kind,
                Audio,
                null,
                new PlannedSound(rules.TargetAudioCodec, spec.AudioBitrateKbps, spec.DownmixToStereo),
                null,
                null,
                file);
        }

        var encodes = spec.VideoCodec is not null;
        var picture = encodes
            ? new PlannedPicture(
                spec.VideoCodec!,
                spec.Crf,
                spec.DownscaleTo?.Width,
                spec.DownscaleTo?.Height,
                rules.MaxFrameRate,
                spec.TonemapToSdr,
                VideoFieldOrder.Deinterlaces(rules.DeinterlaceVideo, media.FieldOrder),
                rules.CropBlackBars,
                rules.NoiseReduction)
            : null;

        return new OptimisationPlan(
            media.Kind,
            encodes ? Encode : rules.TargetContainer is null ? TrackCleanup : Remux,
            picture,
            new PlannedSound(spec.AudioEncoder is null ? null : rules.VideoAudioCodec, spec.AudioBitrateKbps, spec.DownmixToStereo),
            Tracks(media.AudioLanguages, spec.RemoveAudioStreamIndexes),
            Tracks(media.SubtitleLanguages, spec.RemoveSubtitleStreamIndexes),
            file);
    }

    // Null when the languages were never captured: the kept-languages rule then removes nothing,
    // and the plan must not pretend to know which tracks there are.
    private static IReadOnlyList<PlannedTrack>? Tracks(IReadOnlyList<string?>? languages, IReadOnlyList<int>? removed) =>
        languages?.Select((language, index) => new PlannedTrack(index, language, removed?.Contains(index) != true)).ToList();
}
