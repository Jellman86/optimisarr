using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>Per-library tally of how many probed files its rules would optimise vs skip.</summary>
public sealed record CandidateSummary(int LibraryId, int Eligible, int Skipped);

/// <summary>One probed file evaluated against its library's rule profile.</summary>
public sealed record Candidate(
    int MediaFileId,
    int? LibraryId,
    string RelativePath,
    long SizeBytes,
    string? VideoCodec,
    int? Height,
    bool IsHdr,
    string MediaKind,
    string? Codec,
    string Profile,
    bool Eligible,
    string Reason);

/// <summary>
/// Turns the probed inventory into optimisation candidates by running the pure
/// <see cref="CandidateEvaluator"/> over each file with its library's rule profile.
/// No FFmpeg is invoked; this only reads what scanning and probing already stored.
/// </summary>
public sealed class CandidateService(OptimisarrDbContext db)
{
    public async Task<IReadOnlyList<Candidate>> EvaluateAsync(int? libraryId, CancellationToken cancellationToken)
    {
        var librariesById = await db.Libraries
            .AsNoTracking()
            .ToDictionaryAsync(library => library.Id, cancellationToken);

        var query = db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Status == MediaFileStatus.Probed);

        if (libraryId is not null)
        {
            query = query.Where(file => file.LibraryId == libraryId);
        }

        var files = await query.OrderBy(file => file.RelativePath).ToListAsync(cancellationToken);

        var history = await LoadHistoryAsync(libraryId, cancellationToken);

        // Stems (a relative path minus its extension) that already have an Optimisarr-produced output
        // in the same library, so a still-eligible original sitting beside its optimised copy isn't
        // transcoded again only to collide with that copy at replacement time. Keyed per library so a
        // same-named title in another library never cross-matches.
        var optimisedStems = files
            .Where(file => !string.IsNullOrEmpty(file.OptimisedMarker))
            .Select(file => (file.LibraryId, Stem: StemOf(file.RelativePath)))
            .ToHashSet();

        // Excluded files are never offered, whatever the rules say. Keyed by path so the exclusion
        // holds across re-scans and is independent of any job history.
        var excludedPaths = await db.Exclusions
            .AsNoTracking()
            .Select(exclusion => exclusion.Path)
            .ToHashSetAsync(cancellationToken);

        var candidates = new List<Candidate>(files.Count);
        foreach (var file in files)
        {
            var library = file.LibraryId is { } id && librariesById.TryGetValue(id, out var match) ? match : null;
            var profile = LibraryRuleResolution.ProfileOf(library);
            var rules = LibraryRuleResolution.Resolve(library);

            var (media, codec) = Describe(file);

            // The rule decision says whether the file is worth optimising; the history
            // overlay then stops a file we've already optimised (or that failed) for its
            // current version being offered again.
            var decision = CandidateEvaluator.Evaluate(media, rules);

            // An unmarked original whose optimised sibling is already on disk is skipped before any
            // transcode; only the original (no marker) is held back, never the marked copy itself.
            var hasOptimisedSibling = string.IsNullOrEmpty(file.OptimisedMarker)
                && optimisedStems.Contains((file.LibraryId, StemOf(file.RelativePath)));
            decision = OptimisedSiblingEvaluator.Apply(decision, hasOptimisedSibling);

            decision = OptimisationHistoryEvaluator.Apply(
                decision,
                history.GetValueOrDefault(file.Id, OptimisationHistory.None),
                file.ModifiedAt);

            // An explicit exclusion overrides every other verdict with a clear, durable reason.
            if (excludedPaths.Contains(file.Path))
            {
                decision = CandidateDecision.Skipped("Excluded — won't be optimised");
            }

            candidates.Add(new Candidate(
                file.Id,
                file.LibraryId,
                file.RelativePath,
                file.SizeBytes,
                file.VideoCodec,
                file.Height,
                file.IsHdr,
                file.MediaKind.ToString(),
                codec,
                profile.ToString(),
                decision.IsEligible,
                decision.Reason));
        }

        return candidates;
    }

    /// <summary>
    /// Re-evaluates a single probed file against its library's <em>current</em> rules — the pre-flight
    /// check a queued job runs before it transcodes. A job can sit in a long backlog while the rules
    /// tighten (e.g. the already-efficient-source floor is added) or the file gains an optimised
    /// sibling; this catches the "this file should not be encoded" cases so the dispatcher skips it
    /// rather than burning an encode the size-saving gate would only reject. It applies the rule
    /// decision, the optimised-sibling skip, and explicit exclusions, but deliberately <em>not</em> the
    /// job-history overlay — a queued job is an explicit intent to run (e.g. a retry). Returns
    /// <c>null</c> when the file is gone or not yet probed, so the caller proceeds and fails naturally.
    /// </summary>
    public async Task<CandidateDecision?> EvaluateFileAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == mediaFileId && f.Status == MediaFileStatus.Probed, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var library = file.LibraryId is { } id
            ? await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            : null;
        var rules = LibraryRuleResolution.Resolve(library);

        var (media, _) = Describe(file);
        var decision = CandidateEvaluator.Evaluate(media, rules);

        var hasOptimisedSibling = string.IsNullOrEmpty(file.OptimisedMarker)
            && await HasOptimisedSiblingAsync(file, cancellationToken);
        decision = OptimisedSiblingEvaluator.Apply(decision, hasOptimisedSibling);

        if (await db.Exclusions.AsNoTracking().AnyAsync(e => e.Path == file.Path, cancellationToken))
        {
            decision = CandidateDecision.Skipped("Excluded — won't be optimised");
        }

        return decision;
    }

    private async Task<bool> HasOptimisedSiblingAsync(MediaFile file, CancellationToken cancellationToken)
    {
        var stem = StemOf(file.RelativePath);

        // SQLite can't strip an extension server-side, so compare stems in memory over just this
        // library's Optimisarr-produced files (a small set), never the whole inventory.
        var markedPaths = await db.MediaFiles.AsNoTracking()
            .Where(f => f.LibraryId == file.LibraryId && f.OptimisedMarker != null && f.Id != file.Id)
            .Select(f => f.RelativePath)
            .ToListAsync(cancellationToken);

        return markedPaths.Any(path => string.Equals(StemOf(path), stem, StringComparison.Ordinal));
    }

    // Project a probed row into the pure evaluator's input plus the codec that drives its eligibility
    // (audio files report their audio codec; video and still-image files carry it as the video codec).
    private static (MediaProperties Media, string? Codec) Describe(MediaFile file)
    {
        var audioCodec = PrimaryAudioCodec(file.AudioCodecs);
        var media = new MediaProperties(
            file.Container,
            file.VideoCodec,
            file.Width,
            file.Height,
            file.SizeBytes,
            file.IsHdr,
            file.RelativePath,
            file.OptimisedMarker,
            file.MediaKind,
            audioCodec,
            file.AudioBitrateKbps,
            file.FrameCount,
            file.DurationSeconds,
            file.IsDolbyVision,
            file.PixelFormat,
            file.BitsPerRawSample,
            file.AttachedPictureCount,
            file.SubtitleTrackCount ?? 0,
            file.MaxAudioChannels,
            AudioTrackSelection.ParseTrackLanguages(file.AudioLanguages));
        var codec = file.MediaKind == MediaKind.Audio ? audioCodec : file.VideoCodec;
        return (media, codec);
    }

    /// <summary>
    /// Per-library eligible/skipped counts, so the Libraries list can show each library's tally
    /// without the caller fetching every probed file row. Reuses the same evaluation as
    /// <see cref="EvaluateAsync"/>; only files belonging to a library are counted.
    /// </summary>
    public async Task<IReadOnlyList<CandidateSummary>> SummariseAsync(CancellationToken cancellationToken)
    {
        var candidates = await EvaluateAsync(libraryId: null, cancellationToken);

        return candidates
            .Where(candidate => candidate.LibraryId is not null)
            .GroupBy(candidate => candidate.LibraryId!.Value)
            .Select(group => new CandidateSummary(
                group.Key,
                group.Count(candidate => candidate.Eligible),
                group.Count(candidate => !candidate.Eligible)))
            .ToList();
    }

    // AudioCodecs is a comma-separated summary (e.g. "flac" or "eac3, aac"); the first entry
    // is the file's primary audio codec, which drives audio eligibility.
    private static string? PrimaryAudioCodec(string? audioCodecs) =>
        string.IsNullOrWhiteSpace(audioCodecs) ? null : audioCodecs.Split(',')[0].Trim();

    // A relative path minus its final extension, so an original and its optimised re-container
    // (e.g. "Show - S01E01.mkv" and "Show - S01E01.mp4") share one key. GetExtension returns ""
    // when there is none, leaving the path unchanged.
    private static string StemOf(string relativePath) =>
        relativePath[..^Path.GetExtension(relativePath).Length];

    /// <summary>
    /// The most recent successful and failed job finish times per media file, used to
    /// decide whether a file has already been handled for its current version.
    /// </summary>
    private async Task<Dictionary<int, OptimisationHistory>> LoadHistoryAsync(
        int? libraryId, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking()
            .Where(job => job.Type == JobType.Normal
                && job.FinishedAt != null
                && (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed));
        if (libraryId is not null)
        {
            query = query.Where(job => job.LibraryId == libraryId);
        }

        // SQLite stores DateTimeOffset as text and can't aggregate it server-side, so
        // fetch the terminal jobs and reduce to the latest per file in memory.
        var rows = await query
            .Select(job => new { job.MediaFileId, job.Status, job.FinishedAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.MediaFileId)
            .ToDictionary(
                group => group.Key,
                group => new OptimisationHistory(
                    LastCompletedAt: group
                        .Where(row => row.Status == JobStatus.Completed)
                        .Max(row => row.FinishedAt),
                    LastFailedAt: group
                        .Where(row => row.Status == JobStatus.Failed)
                        .Max(row => row.FinishedAt)));
    }
}
