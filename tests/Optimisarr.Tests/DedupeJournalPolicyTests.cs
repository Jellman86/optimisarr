using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class DedupeJournalPolicyTests
{
    [Theory]
    [InlineData(DedupeActionState.Prepared)]
    [InlineData(DedupeActionState.Quarantining)]
    [InlineData(DedupeActionState.Restoring)]
    public void An_interrupted_action_requires_attention_and_retains_every_claim(DedupeActionState state)
    {
        Assert.Equal(DedupeActionState.NeedsAttention, DedupeJournalPolicy.AfterRestart(state));
        Assert.True(DedupeJournalPolicy.HoldsClaims(state));
        Assert.True(DedupeJournalPolicy.HoldsClaims(DedupeJournalPolicy.AfterRestart(state)));
    }

    [Theory]
    [InlineData(DedupeActionState.Quarantined)]
    [InlineData(DedupeActionState.NeedsAttention)]
    [InlineData(DedupeActionState.Restored)]
    [InlineData(DedupeActionState.Cancelled)]
    public void Restart_does_not_invent_filesystem_completion(DedupeActionState state) =>
        Assert.Equal(state, DedupeJournalPolicy.AfterRestart(state));

    [Fact]
    public void Only_untouched_preparations_can_be_cancelled_without_recovery()
    {
        Assert.True(DedupeJournalPolicy.CanTransition(DedupeActionState.Prepared, DedupeActionState.Cancelled));
        foreach (var state in new[] { DedupeActionState.Quarantining, DedupeActionState.Quarantined,
            DedupeActionState.Restoring, DedupeActionState.NeedsAttention })
            Assert.False(DedupeJournalPolicy.CanTransition(state, DedupeActionState.Cancelled));
    }

    [Fact]
    public void Recovery_and_completion_require_a_separate_evidence_gate()
    {
        Assert.False(DedupeJournalPolicy.CanTransition(DedupeActionState.Prepared, DedupeActionState.Quarantined));
        Assert.False(DedupeJournalPolicy.CanTransition(DedupeActionState.NeedsAttention, DedupeActionState.Restored));
        Assert.True(DedupeJournalPolicy.CanTransition(DedupeActionState.NeedsAttention, DedupeActionState.Restoring));
        Assert.True(DedupeJournalPolicy.CanTransition(DedupeActionState.Restoring, DedupeActionState.Restored));
        Assert.False(DedupeJournalPolicy.HoldsClaims(DedupeActionState.Restored));
        Assert.False(DedupeJournalPolicy.HoldsClaims(DedupeActionState.Cancelled));
        Assert.True(DedupeJournalPolicy.HoldsClaims(DedupeActionState.Quarantined));
    }

    [Fact]
    public void Unknown_states_fail_closed()
    {
        Assert.True(DedupeJournalPolicy.HoldsClaims((DedupeActionState)999));
        Assert.Equal(DedupeActionState.NeedsAttention, DedupeJournalPolicy.AfterRestart((DedupeActionState)999));
        Assert.False(DedupeJournalPolicy.CanTransition((DedupeActionState)999, DedupeActionState.Cancelled));
    }
    private static DedupePreparation Preparation() => new(Guid.NewGuid(),1,new('a',64),new('b',64),
    [
        new(1,1,"/library","/library/keeper",null,"disk:1",42,1,new('c',64),true),
        new(2,1,"/library","/library/copy","/library/quarantine/copy","disk:2",42,1,new('c',64),false)
    ]);

    [Fact]
    public void Preparation_requires_a_keeper_and_copy_with_matching_complete_file_evidence()
    {
        var plan = Preparation();
        Assert.True(DedupePreparationPolicy.IsValid(plan));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { IsKeeper = true,QuarantinePath = null }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { Sha256 = new('d',64) }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { SizeBytes = 43 }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { Identity = plan.Files[0].Identity }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { Identity = "" }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { QuarantinePath = null }] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0] with { QuarantinePath = "/unexpected" },plan.Files[1]] }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { ReviewKey = "cached preference" }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Id = Guid.Empty }));
        Assert.False(DedupePreparationPolicy.IsValid(plan with { Files = [plan.Files[0],plan.Files[1] with { MediaFileId = 1 }] }));
    }

    [Fact]
    public void Plan_signature_binds_every_restore_path_identity_role_and_generation()
    {
        var plan = Preparation();
        var signature = DedupePreparationPolicy.Signature(plan);
        Assert.Equal(signature,DedupePreparationPolicy.Signature(plan with { Files = plan.Files.Reverse().ToArray() }));
        foreach (var file in new[]
        {
            plan.Files[1] with { Path = "/library/new-copy" },
            plan.Files[1] with { QuarantinePath = "/library/other-quarantine" },
            plan.Files[1] with { Root = "/another-library" },
            plan.Files[1] with { Identity = "disk:3" },
            plan.Files[1] with { ModifiedUtcTicks = 2 },
            plan.Files[1] with { IsKeeper = true },
            plan.Files[1] with { Sha256 = new('d',64) }
        }) Assert.NotEqual(signature,DedupePreparationPolicy.Signature(plan with { Files = [plan.Files[0],file] }));
    }

}
