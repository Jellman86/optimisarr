using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class DedupeActionPreflightTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const int Keeper = 1, Copy = 2;

    // Canonical absolute paths on every platform; nothing here touches the filesystem.
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "films"));

    private static readonly DedupePreparation Plan = new(Guid.NewGuid(), 7, new string('c', 64), new string('d', 64),
    [
        new(Keeper, 7, Root, Path.Combine(Root, "Film.mkv"), null, "dev1:100", 4096, 638_000_000_000_000_000, Hash, true),
        new(Copy, 7, Root, Path.Combine(Root, "Copy", "Film.mkv"), Path.Combine(Root, ".optimisarr-quarantine", "Film.mkv"),
            "dev1:200", 4096, 638_000_000_000_000_000, Hash, false),
    ]);

    private static readonly DedupeActionContext Ready = new(ManagerConnected: false, StorageQualified: true, BusyMediaFileIds: new HashSet<int>());

    private static DedupeFileObservation Observed(int id) => new(id, Exists: true, IsLink: false, HardLinkCount: 1,
        Identity: id == Keeper ? "dev1:100" : "dev1:200", SizeBytes: 4096, ModifiedUtcTicks: 638_000_000_000_000_000,
        FreshSha256: Hash, VolumeId: "dev1", ParentWritable: true, QuarantineDestinationExists: false, QuarantineVolumeId: "dev1",
        IsIndependentFile: true);

    private static DedupePreflightResult Evaluate(DedupeFileObservation? keeper = null, DedupeFileObservation? copy = null,
        DedupeActionContext? context = null) =>
        DedupeActionPreflight.Evaluate(Plan, [keeper ?? Observed(Keeper), copy ?? Observed(Copy)], context ?? Ready);

    private static void AssertRefused(DedupePreflightResult result, int? mediaFileId, DedupePreflightRefusal refusal)
    {
        Assert.False(result.Allowed);
        Assert.Contains(new DedupePreflightIssue(mediaFileId, refusal), result.Issues);
    }

    [Fact]
    public void Unchanged_fully_read_files_on_one_writable_filesystem_may_move() =>
        Assert.True(Evaluate().Allowed);

    [Fact]
    public void An_invalid_plan_is_refused_before_anything_else_is_judged()
    {
        var result = DedupeActionPreflight.Evaluate(Plan with { Files = [Plan.Files[0]] }, [Observed(Keeper)], Ready);
        Assert.Equal([new DedupePreflightIssue(null, DedupePreflightRefusal.InvalidPlan)], result.Issues);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("traversing")]
    [InlineData("onto-a-source")]
    public void A_plan_whose_quarantine_destination_breaks_the_journal_path_rules_is_refused(string destination)
    {
        var copy = Plan.Files[1] with
        {
            QuarantinePath = destination switch
            {
                "relative" => "quarantine/Film.mkv",
                "traversing" => Path.Combine(Root, "..", "elsewhere", "Film.mkv"),
                _ => Plan.Files[0].Path,
            },
        };
        var result = DedupeActionPreflight.Evaluate(Plan with { Files = [Plan.Files[0], copy] }, [Observed(Keeper), Observed(Copy)], Ready);
        Assert.Equal([new DedupePreflightIssue(null, DedupePreflightRefusal.InvalidPlan)], result.Issues);
    }

    [Fact]
    public void Two_copies_can_never_share_one_quarantine_destination()
    {
        var second = Plan.Files[1] with { MediaFileId = 3, Path = Path.Combine(Root, "Other", "Film.mkv"), Identity = "dev1:300" };
        var result = DedupeActionPreflight.Evaluate(Plan with { Files = [.. Plan.Files, second] },
            [Observed(Keeper), Observed(Copy), Observed(Copy) with { MediaFileId = 3, Identity = "dev1:300" }], Ready);
        Assert.Equal([new DedupePreflightIssue(null, DedupePreflightRefusal.InvalidPlan)], result.Issues);
    }

    [Fact]
    public void A_bundle_member_or_unclassified_file_is_refused() =>
        AssertRefused(Evaluate(keeper: Observed(Keeper) with { IsIndependentFile = false }), Keeper, DedupePreflightRefusal.NotIndependent);

    [Fact]
    public void A_library_connected_to_a_media_manager_never_moves_files() =>
        AssertRefused(Evaluate(context: Ready with { ManagerConnected = true }), null, DedupePreflightRefusal.ManagerConnected);

    [Fact]
    public void Unqualified_storage_never_moves_files() =>
        AssertRefused(Evaluate(context: Ready with { StorageQualified = false }), null, DedupePreflightRefusal.StorageUnqualified);

    [Fact]
    public void A_file_with_active_work_is_refused() =>
        AssertRefused(Evaluate(context: Ready with { BusyMediaFileIds = new HashSet<int> { Keeper } }), Keeper, DedupePreflightRefusal.InUse);

    [Fact]
    public void A_planned_file_that_was_not_observed_is_refused() =>
        AssertRefused(DedupeActionPreflight.Evaluate(Plan, [Observed(Keeper)], Ready), Copy, DedupePreflightRefusal.NotObserved);

    [Fact]
    public void A_missing_keeper_stops_the_action() =>
        AssertRefused(Evaluate(keeper: Observed(Keeper) with { Exists = false }), Keeper, DedupePreflightRefusal.Missing);

    [Fact]
    public void Links_and_hard_linked_files_are_refused_rather_than_followed()
    {
        AssertRefused(Evaluate(copy: Observed(Copy) with { IsLink = true }), Copy, DedupePreflightRefusal.Link);
        AssertRefused(Evaluate(copy: Observed(Copy) with { HardLinkCount = 2 }), Copy, DedupePreflightRefusal.HardLinked);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("size")]
    [InlineData("modified")]
    public void A_file_changed_since_review_is_refused(string change)
    {
        var copy = change switch
        {
            "identity" => Observed(Copy) with { Identity = "dev1:999" },
            "size" => Observed(Copy) with { SizeBytes = 4097 },
            _ => Observed(Copy) with { ModifiedUtcTicks = 638_000_000_000_000_001 },
        };
        AssertRefused(Evaluate(copy: copy), Copy, DedupePreflightRefusal.Changed);
    }

    [Fact]
    public void A_file_not_read_in_full_now_is_refused_even_when_its_metadata_matches() =>
        AssertRefused(Evaluate(keeper: Observed(Keeper) with { FreshSha256 = null }), Keeper, DedupePreflightRefusal.NotRead);

    [Fact]
    public void Content_that_no_longer_matches_the_reviewed_hash_is_refused() =>
        AssertRefused(Evaluate(keeper: Observed(Keeper) with { FreshSha256 = OtherHash }), Keeper, DedupePreflightRefusal.ContentDiffers);

    [Fact]
    public void Two_paths_to_one_physical_file_are_never_treated_as_a_keeper_and_a_copy() =>
        AssertRefused(Evaluate(copy: Observed(Copy) with { Identity = "dev1:100" }), Copy, DedupePreflightRefusal.SamePhysicalFile);

    [Fact]
    public void A_copy_whose_folder_cannot_be_written_is_refused() =>
        AssertRefused(Evaluate(copy: Observed(Copy) with { ParentWritable = false }), Copy, DedupePreflightRefusal.NotWritable);

    [Fact]
    public void A_keeper_is_never_judged_on_move_conditions_because_it_never_moves() =>
        Assert.True(Evaluate(keeper: Observed(Keeper) with
        {
            ParentWritable = false, QuarantineDestinationExists = true, QuarantineVolumeId = "dev2",
        }).Allowed);

    [Fact]
    public void An_occupied_quarantine_destination_is_never_overwritten() =>
        AssertRefused(Evaluate(copy: Observed(Copy) with { QuarantineDestinationExists = true }), Copy, DedupePreflightRefusal.DestinationOccupied);

    [Theory]
    [InlineData("dev1", "dev2")]
    [InlineData("dev1", null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("\t", "\t")]
    public void A_move_that_would_cross_or_cannot_prove_one_filesystem_is_refused(string? volume, string? quarantineVolume) =>
        AssertRefused(Evaluate(copy: Observed(Copy) with { VolumeId = volume, QuarantineVolumeId = quarantineVolume }),
            Copy, DedupePreflightRefusal.CrossFilesystem);

    [Fact]
    public void Every_refusal_is_reported_so_the_operator_sees_all_that_must_change()
    {
        var result = Evaluate(keeper: Observed(Keeper) with { FreshSha256 = null },
            copy: Observed(Copy) with { QuarantineDestinationExists = true }, context: Ready with { ManagerConnected = true });
        Assert.Equal(
        [
            new DedupePreflightIssue(null, DedupePreflightRefusal.ManagerConnected),
            new DedupePreflightIssue(Keeper, DedupePreflightRefusal.NotRead),
            new DedupePreflightIssue(Copy, DedupePreflightRefusal.DestinationOccupied),
        ], result.Issues);
    }
}
