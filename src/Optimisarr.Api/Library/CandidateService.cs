using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

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

        var candidates = new List<Candidate>(files.Count);
        foreach (var file in files)
        {
            var library = file.LibraryId is { } id && librariesById.TryGetValue(id, out var match) ? match : null;
            var profile = LibraryRuleResolution.ProfileOf(library);
            var rules = LibraryRuleResolution.Resolve(library);

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
                file.FrameCount);

            // The codec that matters depends on the kind: audio files report their audio codec,
            // while video and still-image files both carry it as the (probe's) video codec.
            var codec = file.MediaKind == MediaKind.Audio ? audioCodec : file.VideoCodec;

            // The rule decision says whether the file is worth optimising; the history
            // overlay then stops a file we've already optimised (or that failed) for its
            // current version being offered again.
            var decision = CandidateEvaluator.Evaluate(media, rules);
            decision = OptimisationHistoryEvaluator.Apply(
                decision,
                history.GetValueOrDefault(file.Id, OptimisationHistory.None),
                file.ModifiedAt);

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

    // AudioCodecs is a comma-separated summary (e.g. "flac" or "eac3, aac"); the first entry
    // is the file's primary audio codec, which drives audio eligibility.
    private static string? PrimaryAudioCodec(string? audioCodecs) =>
        string.IsNullOrWhiteSpace(audioCodecs) ? null : audioCodecs.Split(',')[0].Trim();

    /// <summary>
    /// The most recent successful and failed job finish times per media file, used to
    /// decide whether a file has already been handled for its current version.
    /// </summary>
    private async Task<Dictionary<int, OptimisationHistory>> LoadHistoryAsync(
        int? libraryId, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking()
            .Where(job => job.FinishedAt != null
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
