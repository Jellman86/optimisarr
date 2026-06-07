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

        var candidates = new List<Candidate>(files.Count);
        foreach (var file in files)
        {
            var library = file.LibraryId is { } id && librariesById.TryGetValue(id, out var match) ? match : null;
            var profile = library?.RuleProfile ?? RuleProfile.ConservativeHevc;
            var rules = RuleResolver.Resolve(profile, ToOverrides(library));

            var media = new MediaProperties(
                file.Container,
                file.VideoCodec,
                file.Width,
                file.Height,
                file.SizeBytes,
                file.IsHdr,
                file.RelativePath);

            var decision = CandidateEvaluator.Evaluate(media, rules);

            candidates.Add(new Candidate(
                file.Id,
                file.LibraryId,
                file.RelativePath,
                file.SizeBytes,
                file.VideoCodec,
                file.Height,
                file.IsHdr,
                profile.ToString(),
                decision.IsEligible,
                decision.Reason));
        }

        return candidates;
    }

    private static RuleOverrides ToOverrides(Data.Library? library)
    {
        if (library is null)
        {
            return RuleOverrides.None;
        }

        return new RuleOverrides
        {
            MinFileSizeBytes = library.MinFileSizeBytes,
            MaxHeight = library.MaxHeight,
            TargetVideoCodec = library.TargetVideoCodec,
            TargetContainer = library.TargetContainer,
            Hdr = library.HdrHandling,
            ExcludePathSegments = ParseExcludePaths(library.ExcludePaths)
        };
    }

    // Operators enter one path substring per line; blank lines are ignored.
    private static IReadOnlyList<string>? ParseExcludePaths(string? excludePaths)
    {
        if (string.IsNullOrWhiteSpace(excludePaths))
        {
            return null;
        }

        var segments = excludePaths
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        return segments.Length > 0 ? segments : null;
    }
}
