namespace Optimisarr.Core.Deduplication;

/// <summary>What the filesystem says about one planned file now, read just before any action.</summary>
/// <param name="FreshSha256">A full read of the file's content now, or null when it was not read.</param>
/// <param name="VolumeId">The filesystem the file lives on; a move must stay on it.</param>
/// <param name="QuarantineVolumeId">The filesystem of the copy's quarantine destination folder.</param>
public sealed record DedupeFileObservation(
    int MediaFileId,
    bool Exists,
    bool IsLink,
    int HardLinkCount,
    string? Identity,
    long SizeBytes,
    long ModifiedUtcTicks,
    string? FreshSha256,
    string? VolumeId,
    bool ParentWritable,
    bool QuarantineDestinationExists,
    string? QuarantineVolumeId);

/// <summary>Facts about the library and the server that decide whether any file may move at all.</summary>
public sealed record DedupeActionContext(
    bool ManagerConnected,
    bool StorageQualified,
    IReadOnlySet<int> BusyMediaFileIds);

public enum DedupePreflightRefusal
{
    InvalidPlan,
    ManagerConnected,
    StorageUnqualified,
    NotObserved,
    Missing,
    Link,
    HardLinked,
    Changed,
    NotRead,
    ContentDiffers,
    SamePhysicalFile,
    InUse,
    NotWritable,
    DestinationOccupied,
    CrossFilesystem,
}

public sealed record DedupePreflightIssue(int? MediaFileId, DedupePreflightRefusal Refusal);

public sealed record DedupePreflightResult(IReadOnlyList<DedupePreflightIssue> Issues)
{
    public bool Allowed => Issues.Count == 0;
}

/// <summary>
/// The last gate before an exact copy is moved to quarantine. A prepared plan records what the
/// review saw; this proves the files are still exactly that, read in full now, and that the move
/// can be a same-filesystem rename that overwrites nothing. Every refusal is reported, not just the
/// first, so the operator sees everything that must change. Refusal is the safe answer to anything
/// unknown: a link, a shared physical file, an unread file or unqualified storage never moves.
/// </summary>
public static class DedupeActionPreflight
{
    public static DedupePreflightResult Evaluate(DedupePreparation plan, IReadOnlyList<DedupeFileObservation> observations,
        DedupeActionContext context)
    {
        if (!DedupePreparationPolicy.IsValid(plan)) return new([new(null, DedupePreflightRefusal.InvalidPlan)]);

        var issues = new List<DedupePreflightIssue>();
        if (context.ManagerConnected) issues.Add(new(null, DedupePreflightRefusal.ManagerConnected));
        if (!context.StorageQualified) issues.Add(new(null, DedupePreflightRefusal.StorageUnqualified));

        var observed = observations.GroupBy(o => o.MediaFileId).ToDictionary(g => g.Key, g => g.ToList());
        var currentIdentities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in plan.Files)
        {
            void Refuse(DedupePreflightRefusal refusal) => issues.Add(new(file.MediaFileId, refusal));
            if (context.BusyMediaFileIds.Contains(file.MediaFileId)) Refuse(DedupePreflightRefusal.InUse);
            if (!observed.TryGetValue(file.MediaFileId, out var matches) || matches.Count != 1)
            {
                Refuse(DedupePreflightRefusal.NotObserved);
                continue;
            }

            var now = matches[0];
            if (!now.Exists)
            {
                Refuse(DedupePreflightRefusal.Missing);
                continue;
            }
            if (now.IsLink) Refuse(DedupePreflightRefusal.Link);
            if (now.HardLinkCount != 1) Refuse(DedupePreflightRefusal.HardLinked);
            if (now.Identity != file.Identity || now.SizeBytes != file.SizeBytes || now.ModifiedUtcTicks != file.ModifiedUtcTicks)
                Refuse(DedupePreflightRefusal.Changed);
            if (now.FreshSha256 is null) Refuse(DedupePreflightRefusal.NotRead);
            else if (!string.Equals(now.FreshSha256, file.Sha256, StringComparison.Ordinal)) Refuse(DedupePreflightRefusal.ContentDiffers);
            if (now.Identity is { } identity && !currentIdentities.TryAdd(identity, file.MediaFileId))
                Refuse(DedupePreflightRefusal.SamePhysicalFile);

            if (file.IsKeeper) continue;
            if (!now.ParentWritable) Refuse(DedupePreflightRefusal.NotWritable);
            if (now.QuarantineDestinationExists) Refuse(DedupePreflightRefusal.DestinationOccupied);
            if (now.VolumeId is null || now.QuarantineVolumeId is null
                || !string.Equals(now.VolumeId, now.QuarantineVolumeId, StringComparison.Ordinal))
                Refuse(DedupePreflightRefusal.CrossFilesystem);
        }
        return new(issues);
    }
}
