using Optimisarr.Sidecar.Core.Session;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Optimisarr.Sidecar.Core.Tests;

[CollectionDefinition("Native scratch ownership", DisableParallelization = true)]
public sealed class NativeScratchOwnershipCollection;

[Collection("Native scratch ownership")]
public sealed class NativeScratchWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-ownership-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Lease = Guid.Parse("81223344-1234-4321-8123-123456789012");
    public NativeScratchWorkspaceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Work => Path.Combine(_root, "work");

    [Fact]
    public void A_live_owner_is_never_reclaimed_even_when_its_files_are_old_and_idle()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var source = Path.Combine(owner.DirectoryPath, "source");
        File.WriteAllText(source, "owned source copy");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddYears(-1));
        var result = NativeScratchWorkspace.Recover(Work);
        Assert.Equal(1, result.Busy);
        Assert.Equal(0, result.Removed);
        Assert.Equal("owned source copy", File.ReadAllText(source));
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void An_abandoned_matching_owner_can_be_recovered_immediately_without_an_age_heuristic()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        File.WriteAllText(Path.Combine(path, "source"), "owned copy");
        owner.Dispose();
        Assert.True(Directory.Exists(path));
        Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.False(Directory.Exists(path));
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.Single(Directory.GetDirectories(Work), path => Path.GetFileName(path) == NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public void Cleanup_is_idempotent_and_removes_only_the_owned_work_tree()
    {
        File.WriteAllText(Path.Combine(_root, "personal-source"), "keep original");
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        Directory.CreateDirectory(Path.Combine(owner.DirectoryPath, "references"));
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "references", "pcm"), "disposable");
        Assert.True(owner.Cleanup());
        Assert.True(owner.Cleanup());
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(_root, "personal-source")));
    }

    [Fact]
    public void Concurrent_attempts_for_the_same_job_and_lease_have_distinct_owned_directories()
    {
        using var one = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        using var two = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        Assert.NotEqual(one.DirectoryPath, two.DirectoryPath);
        Assert.Equal(2, NativeScratchWorkspace.Recover(Work).Busy);
        Assert.True(one.Cleanup());
        Assert.True(Directory.Exists(two.DirectoryPath));
        Assert.True(two.Cleanup());
    }

    [Fact]
    public void Capability_probes_use_the_same_ownership_without_inventing_a_job_or_lease()
    {
        using var owner = NativeScratchWorkspace.CreateCapability(Work);
        Assert.StartsWith("native-capability-", Path.GetFileName(owner.DirectoryPath));
        Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Busy);
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void Legacy_unknown_and_unrecorded_work_directories_are_never_adopted()
    {
        Directory.CreateDirectory(Work);
        foreach (var leaf in new[] { "job-12", "lease-123", "native-job-12-unknown", "personal" })
        {
            Directory.CreateDirectory(Path.Combine(Work, leaf));
            File.WriteAllText(Path.Combine(Work, leaf, "source"), leaf);
        }
        var result = NativeScratchWorkspace.Recover(Work);
        Assert.Equal(0, result.Removed);
        Assert.Equal(4, Directory.GetDirectories(Work).Length);
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("record")]
    public void Corrupt_or_ambiguous_ownership_evidence_retains_all_files(string changed)
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        var evidence = changed == "record" ? owner.RecordPath : Path.Combine(path, NativeScratchWorkspace.MarkerName);
        owner.Dispose();
        File.WriteAllText(Path.Combine(path, "source"), "keep evidence");
        File.AppendAllText(evidence, "\n{}");
        var result = NativeScratchWorkspace.Recover(Work);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Deferred + result.Invalid);
        Assert.Equal("keep evidence", File.ReadAllText(Path.Combine(path, "source")));
    }

    [Fact]
    public void A_replaced_directory_cannot_be_deleted_by_cloning_its_ownership_marker()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        var marker = File.ReadAllBytes(Path.Combine(path, NativeScratchWorkspace.MarkerName));
        owner.Dispose();
        Directory.Move(path, path + "-retained");
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, NativeScratchWorkspace.MarkerName), marker);
        File.WriteAllText(Path.Combine(path, "unrelated"), "keep");
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "unrelated")));
        Assert.True(Directory.Exists(path + "-retained"));
    }

    [Fact]
    public void A_copied_record_cannot_bypass_a_live_owners_kernel_lock()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Move(owner.RecordPath, owner.RecordPath + ".old"));
            Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Busy);
            Assert.True(owner.Cleanup());
            return;
        }
        var record = owner.RecordBytes;
        File.Move(owner.RecordPath, owner.RecordPath + ".old");
        File.WriteAllBytes(owner.RecordPath, record);
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.True(Directory.Exists(owner.DirectoryPath));
        Assert.False(owner.Cleanup());
    }

    [Fact]
    public void A_redirected_work_tree_is_retained_and_its_target_is_untouched()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        owner.Dispose();
        Directory.Move(path, path + "-retained");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "original"), "keep");
        Directory.CreateSymbolicLink(path, outside);
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "original")));
    }

    [Fact]
    public void A_redirected_descendant_defers_the_whole_tree_before_deleting_any_evidence()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        owner.Dispose();
        File.WriteAllText(Path.Combine(path, "candidate"), "keep evidence");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "original"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(path, "redirect"), outside);
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.Equal("keep evidence", File.ReadAllText(Path.Combine(path, "candidate")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "original")));
    }

    [Fact]
    public void Missing_roots_are_a_recovery_no_op_and_do_not_create_files()
    {
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public void Invalid_job_identities_are_refused_before_creating_scratch()
    {
        Assert.Throws<ArgumentException>(() => NativeScratchWorkspace.CreateJob(Work, 0, Lease));
        Assert.Throws<ArgumentException>(() => NativeScratchWorkspace.CreateJob(Work, 12, Guid.Empty));
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public void Recovery_pages_are_bounded_without_permanently_starving_later_owners()
    {
        for (var index = 0; index < 3; index++)
            NativeScratchWorkspace.CreateJob(Work, index + 1, Lease).Dispose();
        var first = NativeScratchWorkspace.Recover(Work, maximumOwners: 1);
        Assert.Equal(1, first.Removed);
        Assert.NotNull(first.NextOwner);
        var second = NativeScratchWorkspace.Recover(Work, maximumOwners: 1, afterOwner: first.NextOwner);
        Assert.Equal(1, second.Removed);
        var third = NativeScratchWorkspace.Recover(Work, maximumOwners: 1, afterOwner: second.NextOwner);
        Assert.Equal(1, third.Removed);
        Assert.Null(third.NextOwner);
        Assert.Single(Directory.GetDirectories(Work), path => Path.GetFileName(path) == NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public void Ownership_records_and_markers_are_private_before_any_media_is_written()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(owner.RecordPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(owner.DirectoryPath, NativeScratchWorkspace.MarkerName)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(owner.DirectoryPath));
        }
        Assert.True(owner.Cleanup());
    }

    [Fact]
    public void A_recorded_cleanup_can_resume_after_its_marker_was_already_removed()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        var record = Encoding.UTF8.GetString(owner.RecordBytes);
        owner.Dispose();
        Assert.Contains("\"State\":\"active\"", record);
        File.WriteAllText(owner.RecordPath, record.Replace("\"State\":\"active\"", "\"State\":\"deleting\""));
        File.Delete(Path.Combine(path, NativeScratchWorkspace.MarkerName));
        File.WriteAllText(Path.Combine(path, "remaining"), "owned copy");
        Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void A_recorded_cleanup_can_retire_after_the_tree_was_removed_before_the_record()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var path = owner.DirectoryPath;
        var record = Encoding.UTF8.GetString(owner.RecordBytes);
        owner.Dispose();
        Assert.Contains("\"State\":\"active\"", record);
        File.WriteAllText(owner.RecordPath, record.Replace("\"State\":\"active\"", "\"State\":\"deleting\""));
        Directory.Delete(path, true);
        Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.False(File.Exists(owner.RecordPath));
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
    }

    [Fact]
    public void A_partial_cleanup_keeps_its_intent_and_can_retry_after_the_io_problem_is_repaired()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var candidate = Path.Combine(owner.DirectoryPath, "candidate");
        File.WriteAllText(candidate, "disposable");
        var nested = Path.Combine(owner.DirectoryPath, "references");
        Directory.CreateDirectory(nested);
        var blocked = Path.Combine(nested, "pcm");
        File.WriteAllText(blocked, "owned copy");
        if (OperatingSystem.IsWindows()) File.SetAttributes(blocked, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(owner.Cleanup());
            Assert.False(File.Exists(candidate));
            Assert.True(File.Exists(blocked));
            Assert.Contains("\"State\":\"deleting\"", File.ReadAllText(owner.RecordPath));
        }
        finally
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(blocked, FileAttributes.Normal);
            else File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.True(owner.Cleanup());
        Assert.False(Directory.Exists(owner.DirectoryPath));
    }

    [Fact]
    public async Task A_surviving_child_keeps_ownership_after_its_parent_is_forcibly_stopped()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var test = typeof(NativeScratchWorkspaceTests).Assembly.Location;
        using var parent = new Process { StartInfo = new(host) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(test, ".runtimeconfig.json"), "--depsfile",
                     Path.ChangeExtension(test, ".deps.json"), typeof(ScratchFixture.Program).Assembly.Location, "parent", Work,
                     host, Path.ChangeExtension(test, ".runtimeconfig.json"), Path.ChangeExtension(test, ".deps.json") })
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
            child = Process.GetProcessById(reply.RootElement.GetProperty("childId").GetInt32());
            var directory = reply.RootElement.GetProperty("directory").GetString()!;
            Assert.False(child.HasExited);
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Busy);
            Assert.Equal("owned test copy", File.ReadAllText(Path.Combine(directory, "source")));
            using (var other = NativeScratchWorkspace.CreateJob(Work, 43, Lease)) Assert.True(other.Cleanup());
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Removed);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(CancellationToken.None); }
            if (child is not null) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); } child.Dispose(); }
        }
    }

    [Fact]
    public void A_shared_file_defers_cleanup_without_unlinking_a_personal_original_or_other_evidence()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var original = Path.Combine(_root, "original");
        File.WriteAllText(original, "keep original");
        var linked = Path.Combine(owner.DirectoryPath, "linked");
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(linked, original, IntPtr.Zero) : Link(original, linked) == 0);
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "candidate"), "keep evidence");
        Assert.False(owner.Cleanup());
        Assert.Equal("keep original", File.ReadAllText(original));
        Assert.Equal("keep evidence", File.ReadAllText(Path.Combine(owner.DirectoryPath, "candidate")));
    }

    [Fact]
    public void A_redirected_record_cannot_supply_cleanup_authority()
    {
        var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        owner.Dispose();
        var original = Path.Combine(_root, "original");
        File.WriteAllText(original, "keep original");
        File.Move(owner.RecordPath, owner.RecordPath + ".retained");
        File.CreateSymbolicLink(owner.RecordPath, original);
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.Equal("keep original", File.ReadAllText(original));
        Assert.True(Directory.Exists(owner.DirectoryPath));
    }

    [Fact]
    public void A_replaced_coordination_lock_does_not_authorize_old_records()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var gate = Path.Combine(Work, NativeScratchWorkspace.ControlDirectoryName, "root.lock");
        File.Move(gate, gate + ".retained");
        File.WriteAllBytes(gate, []);
        Assert.False(owner.Cleanup());
        Assert.Equal(0, NativeScratchWorkspace.Recover(Work).Removed);
        Assert.True(Directory.Exists(owner.DirectoryPath));
    }

    [Fact]
    public void Excessive_trees_are_retained_before_any_file_is_deleted()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        for (var index = 0; index < 129; index++) File.WriteAllText(Path.Combine(owner.DirectoryPath, $"file-{index}"), "keep");
        Assert.False(owner.Cleanup());
        Assert.Equal(130, Directory.GetFiles(owner.DirectoryPath).Length);
    }

    [Fact]
    public void Unknown_control_names_cannot_consume_every_recovery_page_or_become_an_invalid_cursor()
    {
        using var owner = NativeScratchWorkspace.CreateCapability(Work);
        owner.Dispose();
        var control = Path.Combine(Work, NativeScratchWorkspace.ControlDirectoryName);
        for (var index = 0; index < 65; index++) File.WriteAllText(Path.Combine(control, $"owner-00-unknown-{index}.json"), "retained unknown evidence");
        var result = NativeScratchWorkspace.Recover(Work, maximumOwners: 1);
        Assert.Equal(1, result.Removed);
        Assert.Null(result.NextOwner);
        Assert.Equal(66, Directory.GetFiles(control).Length);
        Assert.Equal("retained unknown evidence", File.ReadAllText(Path.Combine(control, "owner-00-unknown-0.json")));
    }

    [Fact]
    public void Unicode_and_quoted_names_remain_inside_the_opened_work_tree()
    {
        using var owner = NativeScratchWorkspace.CreateJob(Path.Combine(Work, "folder ' [字幕]"), 12, Lease);
        File.WriteAllText(Path.Combine(owner.DirectoryPath, "candidate ' [字幕].dat"), "owned copy");
        Assert.True(owner.Cleanup());
        Assert.False(Directory.Exists(owner.DirectoryPath));
    }

    [Fact]
    public void Cancelled_recovery_does_not_create_a_root_or_touch_existing_work()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeScratchWorkspace.Recover(Work, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(Work));
        using var owner = NativeScratchWorkspace.CreateJob(Work, 12, Lease);
        var source = Path.Combine(owner.DirectoryPath, "source");
        File.WriteAllText(source, "retained source copy");
        owner.Dispose();
        Assert.Throws<OperationCanceledException>(() => NativeScratchWorkspace.Recover(Work, cancellationToken: cancellation.Token));
        Assert.Equal("retained source copy", File.ReadAllText(source));
        Assert.Equal(owner.RecordBytes, File.ReadAllBytes(owner.RecordPath));
        Assert.Equal(1, NativeScratchWorkspace.Recover(Work).Removed);
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
