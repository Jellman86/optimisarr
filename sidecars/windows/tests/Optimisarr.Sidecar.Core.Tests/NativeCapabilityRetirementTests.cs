using System.Text.Json.Nodes;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class NativeCapabilityRetirementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-capability-retirement-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void A_capability_owner_holds_its_charge_until_positive_retirement_is_consumed()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(_root, identity, scope);
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "probe-copy"), "owned probe");
        var record = JsonNode.Parse(owner.RecordBytes)!;
        Assert.Equal("capability", record["Purpose"]!.GetValue<string>());
        Assert.Null(record["JobId"]);
        Assert.Null(record["LeaseId"]);
        Assert.True(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Busy);
        Assert.False(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
        Assert.True(owner.Cleanup());
        Assert.True(owner.Cleanup());
        Assert.False(Directory.Exists(owner.DirectoryPath));
        Assert.True(File.Exists(owner.RecordPath));
        Assert.True(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
        Assert.True(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
        Assert.Equal(new NativeScratchRecovery(Deferred: 1), NativeScratchWorkspace.Recover(_root));
        Assert.True(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
        Assert.True(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).AlreadyAbsent);
        Assert.False(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
    }

    [Fact]
    public void Generic_recovery_leaves_an_accounted_capability_for_its_resource_coordinator()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(_root, Guid.NewGuid(), scope);
        owner.Dispose();
        Assert.Equal(new NativeScratchRecovery(Deferred: 1), NativeScratchWorkspace.Recover(_root));
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void Job_and_capability_receipts_are_not_interchangeable_even_with_the_same_owner()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var jobId = Guid.NewGuid();
        var lease = Guid.NewGuid();
        using var job = NativeScratchWorkspace.CreateAccountedJob(_root, 12, lease, jobId, scope);
        job.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedCapability(_root, jobId, scope).Retired);
        Assert.True(job.Cleanup());
        Assert.False(NativeScratchWorkspace.ForgetRetiredCapability(_root, jobId, scope).Consumed);
        var probeId = Guid.NewGuid();
        using var probe = NativeScratchWorkspace.CreateAccountedCapability(_root, probeId, scope);
        probe.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, lease, probeId, expectedScope: scope).Retired);
        Assert.True(probe.Cleanup());
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, lease, probeId, scope).Consumed);
        Assert.True(File.Exists(job.RecordPath));
        Assert.True(File.Exists(probe.RecordPath));
    }

    [Fact]
    public void An_unaccounted_capability_cannot_be_adopted_as_positive_resource_retirement()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        using var owner = NativeScratchWorkspace.CreateCapability(_root);
        var identity = JsonNode.Parse(owner.RecordBytes)!["Owner"]!.GetValue<Guid>();
        owner.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.Equal(1, NativeScratchWorkspace.Recover(_root).Removed);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("control")]
    [InlineData("gate")]
    [InlineData("owner")]
    [InlineData("marker")]
    public void Changed_capability_ownership_never_releases_a_resource_obligation(string changed)
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(_root, identity, scope);
        owner.Dispose();
        var path = changed switch {
            "root" => _root,
            "control" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName),
            "gate" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName, "root.lock"),
            "marker" => Path.Combine(owner.DirectoryPath, NativeScratchWorkspace.MarkerName),
            _ => owner.RecordPath
        };
        var retained = path + "-retained";
        try
        {
            if (Directory.Exists(path)) { Directory.Move(path, retained); Directory.CreateDirectory(path); }
            else { File.Move(path, retained); File.Copy(retained, path); }
            Assert.False(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
            var consumed = NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope);
            Assert.False(consumed.Consumed || consumed.AlreadyAbsent);
            Assert.False(owner.Cleanup());
        }
        finally { if (changed == "root" && Directory.Exists(retained)) Directory.Delete(retained, true); }
    }

    [Fact]
    public void Accounted_capabilities_require_a_nonempty_owner_and_an_explicit_physical_scope()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => NativeScratchWorkspace.CreateAccountedCapability(_root, Guid.Empty, scope));
        Assert.Throws<ArgumentException>(() => NativeScratchWorkspace.RetireAccountedCapability(_root, Guid.Empty, scope));
        Assert.Throws<ArgumentException>(() => NativeScratchWorkspace.ForgetRetiredCapability(_root, Guid.Empty, scope));
        Assert.Throws<ArgumentNullException>(() => NativeScratchWorkspace.CreateAccountedCapability(_root, identity, null!));
        Assert.Throws<ArgumentNullException>(() => NativeScratchWorkspace.RetireAccountedCapability(_root, identity, null!));
        Assert.Throws<ArgumentNullException>(() => NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, null!));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("control")]
    [InlineData("gate")]
    public void Missing_capability_scope_is_not_recreated_by_creation_retirement_or_consumption(string missing)
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        var path = missing switch {
            "root" => _root,
            "control" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName),
            _ => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName, "root.lock")
        };
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
        Assert.ThrowsAny<IOException>(() => NativeScratchWorkspace.CreateAccountedCapability(_root, identity, scope));
        Assert.False(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
        var consumed = NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope);
        Assert.False(consumed.Consumed || consumed.AlreadyAbsent);
        Assert.False(File.Exists(path) || Directory.Exists(path));
    }

    [Fact]
    public void A_capability_nonce_cannot_retire_a_different_scope_or_be_reused_while_unconsumed()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var otherRoot = Path.Combine(_root, "other");
        var otherScope = NativeScratchWorkspace.PrepareAccountingScope(otherRoot);
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(otherRoot, identity, otherScope);
        owner.Dispose();
        Assert.Throws<IOException>(() => NativeScratchWorkspace.CreateAccountedCapability(otherRoot, Guid.NewGuid(), scope));
        Assert.Throws<IOException>(() => NativeScratchWorkspace.CreateAccountedCapability(otherRoot, identity, otherScope));
        Assert.False(NativeScratchWorkspace.RetireAccountedCapability(otherRoot, identity, scope).Retired);
        var consumed = NativeScratchWorkspace.ForgetRetiredCapability(otherRoot, identity, scope);
        Assert.False(consumed.Consumed || consumed.AlreadyAbsent);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void A_capability_retirement_replays_a_crash_after_deletion_but_before_the_retired_receipt()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(_root, identity, scope);
        owner.Dispose();
        var record = JsonNode.Parse(owner.RecordBytes)!.AsObject();
        record["State"] = "deleting";
        File.WriteAllText(owner.RecordPath, record.ToJsonString());
        Directory.Delete(owner.DirectoryPath, true);
        Assert.False(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
        Assert.True(NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope).Retired);
        Assert.True(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
    }

    [Fact]
    public void Pre_cancelled_capability_retirement_leaves_work_and_its_charge_pending()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedCapability(_root, identity, scope);
        owner.Dispose();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeScratchWorkspace.RetireAccountedCapability(_root, identity, scope, cancellation.Token));
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.False(NativeScratchWorkspace.ForgetRetiredCapability(_root, identity, scope).Consumed);
    }
}
