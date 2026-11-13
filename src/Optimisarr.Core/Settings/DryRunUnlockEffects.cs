namespace Optimisarr.Core.Settings;

/// <summary>
/// What turning dry-run off would let this server do straight away, beyond letting a person press
/// Replace. Dry-run holds back more than replacement: deleting quarantined originals and duplicate
/// copies past their time limit, daily duplicate clean-up, and automatic replacement of files
/// already verified. Someone unlocking it should see these before they happen.
/// </summary>
public sealed record DryRunUnlockEffects(
    int RetentionDays,
    int OriginalsToDelete,
    long OriginalBytes,
    int DuplicatesToDelete,
    long DuplicateBytes,
    int LibrariesCleaningDuplicatesDaily,
    int JobsToReplace)
{
    /// <summary>True when unlocking would act on files without anyone pressing anything further.</summary>
    public bool NeedsConfirmation =>
        OriginalsToDelete > 0 || DuplicatesToDelete > 0 || LibrariesCleaningDuplicatesDaily > 0 || JobsToReplace > 0;
}
