using Optimisarr.Sidecar.Core.Session;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class NativeScratchRetirementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-retirement-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Lease = Guid.Parse("81223344-1234-4321-8123-123456789012");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void A_parent_selected_owner_cannot_retire_while_an_inherited_reference_is_live()
    {
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "source"), "owned copy");
        var retirement = NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity);
        Assert.False(retirement.Retired);
        Assert.True(retirement.Busy);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void Successful_cleanup_leaves_durable_positive_retirement_until_the_parent_consumes_it()
    {
        var identity = Guid.NewGuid();
        using var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "source"), "owned copy");
        Assert.True(owner.Cleanup());
        Assert.False(Directory.Exists(owner.DirectoryPath));
        Assert.True(File.Exists(owner.RecordPath));
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 13, Lease, identity).Consumed);
        Assert.True(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
        Assert.True(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).AlreadyAbsent);
        Assert.False(File.Exists(owner.RecordPath));
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
    }

    [Fact]
    public void A_generic_sweep_cannot_erase_a_parents_pending_retirement_receipt()
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        var directory = owner.DirectoryPath;
        owner.Dispose();
        var sweep = NativeScratchWorkspace.Recover(_root);
        Assert.Equal(0, sweep.Removed);
        Assert.Equal(1, sweep.Deferred);
        Assert.True(Directory.Exists(directory));
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.Equal(0, NativeScratchWorkspace.Recover(_root).Removed);
        Assert.True(File.Exists(owner.RecordPath));
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("control")]
    [InlineData("gate")]
    [InlineData("owner")]
    public void Missing_or_replaced_ownership_never_counts_as_positive_retirement(string changed)
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        owner.Dispose();
        var path = changed switch {
            "root" => _root,
            "control" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName),
            "gate" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName, "root.lock"),
            _ => owner.RecordPath
        };
        var retained = path + "-retained";
        if (Directory.Exists(path)) {
            Directory.Move(path, retained);
            Directory.CreateDirectory(path);
        } else {
            File.Move(path, retained);
            File.WriteAllBytes(path, changed == "owner" ? owner.RecordBytes : []);
        }
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
        // Root renames place the retained fixture beside our owned cleanup root.
        if (changed == "root") Directory.Delete(retained, true);
    }

    [Fact]
    public void Wrong_job_lease_or_nonce_cannot_retire_or_consume_another_runs_receipt()
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        owner.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 13, Lease, identity).Retired);
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Guid.NewGuid(), identity).Retired);
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, Guid.NewGuid()).Retired);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Guid.NewGuid(), identity).Consumed);
    }

    [Fact]
    public void An_ordinary_unaccounted_workspace_cannot_be_adopted_as_a_parents_resource_proof()
    {
        using var owner = NativeScratchWorkspace.CreateJob(_root, 12, Lease);
        var identity = Guid.ParseExact(Path.GetFileName(owner.RecordPath)[6..^5], "N");
        owner.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.Equal(1, NativeScratchWorkspace.Recover(_root).Removed);
    }

    [Fact]
    public void A_crash_after_tree_deletion_replays_to_durable_retirement_without_an_unearned_empty_success()
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        owner.Dispose();
        var record = JsonNode.Parse(owner.RecordBytes)!.AsObject();
        record["State"] = "deleting";
        File.WriteAllText(owner.RecordPath, record.ToJsonString());
        Directory.Delete(owner.DirectoryPath, true);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.True(File.Exists(owner.RecordPath));
        Assert.True(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
    }

    [Fact]
    public void An_active_or_corrupt_record_cannot_be_consumed_and_a_nonce_cannot_be_reused()
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        owner.Dispose();
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
        Assert.Throws<IOException>(() => NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity));
        File.AppendAllText(owner.RecordPath, "\n{}");
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
        Assert.True(Directory.Exists(owner.DirectoryPath));
    }

    [Fact]
    public void A_pre_cancelled_retirement_cannot_delete_work_or_persist_a_retired_state()
    {
        var identity = Guid.NewGuid();
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity);
        owner.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity, cancelled.Token));
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity).Consumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_real_inherited_child_vetoes_targeted_retirement_after_its_helper_is_killed(bool capability)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var test = typeof(NativeScratchRetirementTests).Assembly.Location;
        var identity = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        var release = Path.Combine(_root, "release-owned-child");
        var work = Path.Combine(_root, "work");
        using var parent = new Process { StartInfo = new(host) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(test, ".runtimeconfig.json"), "--depsfile",
                     Path.ChangeExtension(test, ".deps.json"), typeof(ScratchFixture.Program).Assembly.Location, "parent", work,
                     host, Path.ChangeExtension(test, ".runtimeconfig.json"), Path.ChangeExtension(test, ".deps.json"), identity.ToString(), release,
                     capability ? "capability" : "job" })
            parent.StartInfo.ArgumentList.Add(arg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? child = null;
        try
        {
            parent.Start();
            var errors = parent.StandardError.ReadToEndAsync(timeout.Token);
            var line = await parent.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail(await errors.WaitAsync(TimeSpan.FromSeconds(1)));
            using var reply = JsonDocument.Parse(line!);
            var scope = JsonSerializer.Deserialize<NativeScratchScope>(reply.RootElement.GetProperty("scope"))!;
            child = Process.GetProcessById(reply.RootElement.GetProperty("childId").GetInt32());
            var directory = reply.RootElement.GetProperty("directory").GetString()!;
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            Assert.False(child.HasExited);
            Assert.True(Retire().Busy);
            Assert.False(Consume().Consumed);
            Assert.Equal(1, NativeScratchWorkspace.Recover(work).Busy);
            Assert.Equal("owned test copy", File.ReadAllText(Path.Combine(directory, "source")));
            await File.WriteAllTextAsync(release, "finish naturally", timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            Assert.True(Retire().Retired);
            Assert.False(Directory.Exists(directory));
            Assert.True(Retire().Retired);
            Assert.Equal(0, NativeScratchWorkspace.Recover(work).Removed);
            Assert.True(Consume().Consumed);

            NativeScratchRetirement Retire() => capability ? NativeScratchWorkspace.RetireAccountedCapability(work, identity, scope)
                : NativeScratchWorkspace.RetireAccountedJob(work, 42, Lease, identity, expectedScope: scope);
            NativeScratchConsumption Consume() => capability ? NativeScratchWorkspace.ForgetRetiredCapability(work, identity, scope)
                : NativeScratchWorkspace.ForgetRetiredJob(work, 42, Lease, identity, scope);
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(CancellationToken.None); }
            if (child is not null) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); } child.Dispose(); }
        }
    }

    [Fact]
    public void A_nonce_copied_to_a_new_physical_scope_cannot_retire_the_parents_original_obligation()
    {
        var expected = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var other = Path.Combine(_root, "another-root");
        var identity = Guid.NewGuid();
        using var wrong = NativeScratchWorkspace.CreateAccountedJob(other, 12, Lease, identity);
        wrong.Dispose();
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(other, 12, Lease, identity, expectedScope: expected).Retired);
        Assert.False(NativeScratchWorkspace.ForgetRetiredJob(other, 12, Lease, identity, expected).Consumed);
        Assert.True(Directory.Exists(wrong.DirectoryPath));
        Assert.Throws<IOException>(() => NativeScratchWorkspace.CreateAccountedJob(other, 13, Lease, Guid.NewGuid(), expected));
        Assert.True(wrong.Cleanup());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("control")]
    [InlineData("gate")]
    public void A_missing_pinned_scope_cannot_be_recreated_by_run_retirement_or_consumption(string missing)
    {
        var expected = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var identity = Guid.NewGuid();
        var path = missing switch {
            "root" => _root,
            "control" => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName),
            _ => Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName, "root.lock")
        };
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
        Assert.ThrowsAny<IOException>(() => NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, identity, expected));
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, 12, Lease, identity, expectedScope: expected).Retired);
        var consumed = NativeScratchWorkspace.ForgetRetiredJob(_root, 12, Lease, identity, expected);
        Assert.False(consumed.Consumed || consumed.AlreadyAbsent);
        Assert.False(File.Exists(path) || Directory.Exists(path));
    }

    [Fact]
    public void A_changed_alias_cannot_authorize_another_valid_scope_with_the_same_run_identity()
    {
        Directory.CreateDirectory(_root);
        var firstRoot = Path.Combine(_root, "first"); Directory.CreateDirectory(firstRoot);
        var secondRoot = Path.Combine(_root, "second"); Directory.CreateDirectory(secondRoot);
        var alias = Path.Combine(_root, "selected-folder");
        Directory.CreateSymbolicLink(alias, firstRoot);
        var expected = NativeScratchWorkspace.PrepareAccountingScope(alias);
        var identity = Guid.NewGuid();
        using var original = NativeScratchWorkspace.CreateAccountedJob(alias, 12, Lease, identity, expected);
        using var other = NativeScratchWorkspace.CreateAccountedJob(secondRoot, 12, Lease, identity);
        other.Dispose();
        Directory.Delete(alias); Directory.CreateSymbolicLink(alias, secondRoot);
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(alias, 12, Lease, identity, expectedScope: expected).Retired);
        var consumed = NativeScratchWorkspace.ForgetRetiredJob(alias, 12, Lease, identity, expected);
        Assert.False(consumed.Consumed || consumed.AlreadyAbsent);
        Assert.Throws<IOException>(() => NativeScratchWorkspace.CreateAccountedJob(alias, 13, Lease, Guid.NewGuid(), expected));
        Assert.True(Directory.Exists(original.DirectoryPath));
        Assert.True(Directory.Exists(other.DirectoryPath));
        Assert.True(original.Cleanup()); Assert.True(other.Cleanup());
    }
}
