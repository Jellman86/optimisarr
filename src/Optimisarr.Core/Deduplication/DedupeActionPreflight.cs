namespace Optimisarr.Core.Deduplication;

/// <summary>What the filesystem says about one planned file now, read just before any action.</summary>
/// <param name="FreshSha256">A full read of the file's content now, or null when it was not read.</param>
/// <param name="VolumeId">The filesystem the file lives on; a move must stay on it.</param>
/// <param name="QuarantineVolumeId">The filesystem of the copy's quarantine destination folder.</param>
/// <param name="QuarantinePathSafe">
/// Every existing folder on the way to the destination is a real folder inside the library: no
/// link, dangling link or file stands in the path. Always false when it could not be established.
/// </param>
/// <param name="ChangeStamp">
/// The file's change time across the full read, so later checks can tell whether it changed since.
/// </param>
/// <param name="IsIndependentFile">
/// The observer found an ordinary standalone media file, not part of a bundle, package or other
/// multi-file asset whose pieces must stay together. Anything the observer cannot classify is false.
/// </param>
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
    string? QuarantineVolumeId,
    bool QuarantinePathSafe,
    bool IsIndependentFile,
    string? ChangeStamp = null);

/// <summary>Facts about the library and the server that decide whether any file may move at all.</summary>
/// <param name="ManagersAnswered">Every enabled Sonarr or Radarr said what it tracks.</param>
/// <param name="TrackedMediaFileIds">Files a manager clearly tracks; moving one would show as missing.</param>
/// <param name="PossiblyTrackedMediaFileIds">Files that share a tracked file's name without clearly being it.</param>
public sealed record DedupeActionContext(
    bool ManagersAnswered,
    IReadOnlySet<int> TrackedMediaFileIds,
    IReadOnlySet<int> PossiblyTrackedMediaFileIds,
    bool StorageQualified,
    IReadOnlySet<int> BusyMediaFileIds);

public enum DedupePreflightRefusal
{
    InvalidPlan,
    ManagerUnavailable,
    TrackedByManager,
    PossiblyTrackedByManager,
    StorageUnqualified,
    NotObserved,
    Missing,
    Link,
    HardLinked,
    NotIndependent,
    Changed,
    NotRead,
    ContentDiffers,
    SamePhysicalFile,
    InUse,
    NotWritable,
    DestinationOccupied,
    UnsafeDestination,
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
/// unknown: a link, a bundle member, a shared physical file, an unread file, an unproven filesystem
/// or unqualified storage never moves. Plans must also pass the journal's path rules.
/// </summary>
public static class DedupeActionPreflight
{
    public static DedupePreflightResult Evaluate(DedupePreparation plan, IReadOnlyList<DedupeFileObservation> observations,
        DedupeActionContext context)
    {
        if (!DedupePreparationPolicy.IsValid(plan) || !DedupePreparationPolicy.HasValidPaths(plan))
            return new([new(null, DedupePreflightRefusal.InvalidPlan)]);

        var issues = new List<DedupePreflightIssue>();
        if (!context.ManagersAnswered) issues.Add(new(null, DedupePreflightRefusal.ManagerUnavailable));
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
            // A keeper stays where it is, so its other names do not matter; a moved copy frees nothing.
            if (now.HardLinkCount != 1 && (!file.IsKeeper || now.HardLinkCount < 1)) Refuse(DedupePreflightRefusal.HardLinked);
            if (!now.IsIndependentFile) Refuse(DedupePreflightRefusal.NotIndependent);
            if (now.Identity != file.Identity || now.SizeBytes != file.SizeBytes || now.ModifiedUtcTicks != file.ModifiedUtcTicks)
                Refuse(DedupePreflightRefusal.Changed);
            if (now.FreshSha256 is null) Refuse(DedupePreflightRefusal.NotRead);
            else if (!string.Equals(now.FreshSha256, file.Sha256, StringComparison.Ordinal)) Refuse(DedupePreflightRefusal.ContentDiffers);
            if (now.Identity is { } identity && !currentIdentities.TryAdd(identity, file.MediaFileId))
                Refuse(DedupePreflightRefusal.SamePhysicalFile);

            if (file.IsKeeper) continue;
            if (context.TrackedMediaFileIds.Contains(file.MediaFileId)) Refuse(DedupePreflightRefusal.TrackedByManager);
            if (context.PossiblyTrackedMediaFileIds.Contains(file.MediaFileId)
                && !plan.Files.Any(other => other.IsKeeper && context.TrackedMediaFileIds.Contains(other.MediaFileId)))
                Refuse(DedupePreflightRefusal.PossiblyTrackedByManager);
            if (!now.ParentWritable) Refuse(DedupePreflightRefusal.NotWritable);
            if (now.QuarantineDestinationExists) Refuse(DedupePreflightRefusal.DestinationOccupied);
            if (!now.QuarantinePathSafe) Refuse(DedupePreflightRefusal.UnsafeDestination);
            if (string.IsNullOrWhiteSpace(now.VolumeId) || string.IsNullOrWhiteSpace(now.QuarantineVolumeId)
                || !string.Equals(now.VolumeId, now.QuarantineVolumeId, StringComparison.Ordinal))
                Refuse(DedupePreflightRefusal.CrossFilesystem);
        }
        return new(issues);
    }
}
