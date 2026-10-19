using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

// Experimental NTFS candidate: these tests prove same-OS visibility, identity, security and
// crash-refusal behaviour only. They are not power-loss durability evidence.
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeWorkJournalStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-journal-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "journal");
    private string Checkpoint => Path.Combine(_root, "authority");
    private string LedgerFile => Path.Combine(Journal, "ledger.json");
    private string CheckpointFile => Path.Combine(Checkpoint, "checkpoint.json");
    private string LockFile => Path.Combine(Journal, "coordinator.lock");
    public WindowsNativeWorkJournalStorageTests() => Directory.CreateDirectory(_root);
    private NativeWorkJournal Open() => NativeWorkJournal.OpenExisting(WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint));
    private static NativeWorkSnapshot Add(NativeWorkSnapshot snapshot, string root) => snapshot with {
        Revision = snapshot.Revision + 1,
        Obligations = [new(Guid.NewGuid(), NativeWorkPurpose.Capability, null, null, root,
            new("root:1", "control:1", "gate:1"), "volume:1", 100, 200, false, NativeWorkPhase.Intent)] };
    private Task<int> Commit(NativeWorkJournal journal) => journal.ExecuteAsync<int>((scope, _) => {
        scope.Commit(Add(scope.Snapshot, _root)); return Task.CompletedTask;
    }, _ => Task.FromResult(42), (_, _) => Task.CompletedTask);

    private static SecurityIdentifier[] Trusted() =>
    [
        WindowsIdentity.GetCurrent().User!,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    private static void AssertPrivateSecurity(FileSystemSecurity security, string path)
    {
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, owner.Value);
        Assert.True(security.AreAccessRulesProtected, path);
        var trusted = Trusted().Select(sid => sid.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, trusted);
            Assert.False(rule.IsInherited, path);
        }
    }

    private static void GrantEveryone(string path, bool directory, FileSystemRights rights)
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (directory)
        {
            var info = new DirectoryInfo(path); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, rights, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    [WindowsStorageFact]
    public async Task Provisioning_creates_private_independent_authority_and_restart_retains_exact_debt()
    {
        var storage = WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        var binding = storage.Binding;
        await using (var journal = NativeWorkJournal.OpenExisting(storage))
        {
            Assert.Equal(0, journal.Snapshot.Revision);
            await Commit(journal);
            Assert.Equal(1, journal.Snapshot.Revision);
        }
        using var reopened = WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint);
        Assert.Equal(binding, reopened.Binding);
        var restored = NativeWorkLedger.Decode(reopened.ReadLedger());
        Assert.Equal(1, restored.Revision); Assert.Single(restored.Obligations);
        reopened.Dispose();
        foreach (var path in new[] { Journal, Checkpoint }) AssertPrivateSecurity(new DirectoryInfo(path).GetAccessControl(), path);
        foreach (var path in new[] { LedgerFile, CheckpointFile, LockFile }) AssertPrivateSecurity(new FileInfo(path).GetAccessControl(), path);
    }

    [WindowsStorageFact]
    public async Task A_second_owner_cannot_open_or_provision_until_the_stable_lock_is_released()
    {
        var first = NativeWorkJournal.OpenExisting(WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        var before = File.ReadAllBytes(LedgerFile);
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint));
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        Assert.Equal(before, File.ReadAllBytes(LedgerFile));
        await first.DisposeAsync();
        await using var second = Open();
        Assert.Equal(0, second.Snapshot.Revision);
    }

    [WindowsStorageFact]
    public async Task A_child_process_cannot_inherit_the_coordinator_lock_past_a_forced_parent_death()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var test = typeof(WindowsNativeWorkJournalStorageTests).Assembly.Location;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var parent = new Process { StartInfo = new(host) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(test, ".runtimeconfig.json"), "--depsfile",
                     Path.ChangeExtension(test, ".deps.json"), typeof(ScratchFixture.Program).Assembly.Location, "journal-parent",
                     Journal, Checkpoint, Path.Combine(_root, "child-ready"), host,
                     Path.ChangeExtension(test, ".runtimeconfig.json"), Path.ChangeExtension(test, ".deps.json") })
            parent.StartInfo.ArgumentList.Add(arg);
        Process? child = null;
        try
        {
            parent.Start();
            var errors = parent.StandardError.ReadToEndAsync(timeout.Token);
            var line = await parent.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail(await errors.WaitAsync(TimeSpan.FromSeconds(1)));
            using var reply = JsonDocument.Parse(line!);
            child = Process.GetProcessById(reply.RootElement.GetProperty("childId").GetInt32());
            Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint));
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            Assert.False(child.HasExited);
            // Handle release at process teardown is not instantaneous on every
            // host; retry briefly. The surviving child would hold an inherited
            // coordinator for its whole 30-second life, far past this window,
            // so a genuine inheritance leak still fails here.
            NativeWorkJournal? journal = null;
            for (var attempt = 0; attempt < 20 && journal is null; attempt++)
            {
                try { journal = Open(); }
                catch (IOException) { await Task.Delay(250, timeout.Token); }
            }
            Assert.NotNull(journal);
            await using (journal)
                Assert.Equal(0, journal.Snapshot.Revision);
        }
        finally
        {
            if (child is not null && !child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(CancellationToken.None); }
        }
    }

    [WindowsStorageTheory]
    [InlineData("ledger")]
    [InlineData("checkpoint")]
    [InlineData("lock")]
    public async Task Missing_metadata_cannot_reinitialize_even_an_empty_existing_store(string missing)
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var file = missing switch { "ledger" => LedgerFile, "checkpoint" => CheckpointFile, _ => LockFile };
        File.Delete(file);
        var error = await Record.ExceptionAsync(async () => { await using var _ = Open(); });
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.False(File.Exists(file));
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
    }

    [WindowsStorageFact]
    public async Task An_older_empty_ledger_cannot_hide_independently_committed_debt()
    {
        var journal = NativeWorkJournal.OpenExisting(WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        var empty = File.ReadAllBytes(LedgerFile);
        await Commit(journal); await journal.DisposeAsync();
        File.WriteAllBytes(LedgerFile, empty);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [WindowsStorageTheory]
    [InlineData("journal", true)]
    [InlineData("checkpoint", true)]
    [InlineData("lock", true)]
    [InlineData("ledger", true)]
    [InlineData("authority-file", true)]
    [InlineData("journal", false)]
    [InlineData("checkpoint", false)]
    [InlineData("lock", false)]
    [InlineData("ledger", false)]
    [InlineData("authority-file", false)]
    public void Widened_private_access_refuses_accounting(string changed, bool write)
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = changed switch { "journal" => Journal, "checkpoint" => Checkpoint, "lock" => LockFile,
            "ledger" => LedgerFile, _ => CheckpointFile };
        GrantEveryone(path, changed is "journal" or "checkpoint", write ? FileSystemRights.WriteData : FileSystemRights.ReadData);
        Assert.ThrowsAny<IOException>(() => Open());
    }

    [WindowsStorageTheory]
    [InlineData("ledger")]
    [InlineData("checkpoint")]
    [InlineData("lock")]
    public void Redirected_or_shared_files_cannot_supply_or_receive_authority(string changed)
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = changed switch { "ledger" => LedgerFile, "checkpoint" => CheckpointFile, _ => LockFile };
        var saved = File.ReadAllBytes(path); var victim = Path.Combine(_root, "sentinel"); File.WriteAllBytes(victim, saved);
        File.Delete(path); File.CreateSymbolicLink(path, victim);
        Assert.ThrowsAny<IOException>(() => Open()); Assert.Equal(saved, File.ReadAllBytes(victim));
        File.Delete(path); Assert.True(CreateHardLinkW(path, victim, IntPtr.Zero));
        Assert.ThrowsAny<IOException>(() => Open()); Assert.Equal(saved, File.ReadAllBytes(victim));
    }

    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public void Oversized_or_corrupt_authority_is_refused_without_an_empty_fallback(bool checkpoint)
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = checkpoint ? CheckpointFile : LedgerFile;
        File.WriteAllBytes(path, new byte[(checkpoint ? NativeWorkCheckpointProtocol.MaximumBytes : NativeWorkLedger.MaximumBytes) + 1]);
        Assert.ThrowsAny<IOException>(() => Open());
    }

    [WindowsStorageFact]
    public void Journal_and_checkpoint_directories_must_be_distinct_and_nonoverlapping()
    {
        Assert.Throws<ArgumentException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Journal));
        Assert.Throws<ArgumentException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Path.Combine(Journal, "authority")));
        Assert.Throws<ArgumentException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Path.Combine(Checkpoint, "journal"), Checkpoint));
        Assert.Throws<ArgumentException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Journal.ToUpperInvariant()));
        Assert.False(Directory.Exists(Journal)); Assert.False(Directory.Exists(Checkpoint));
    }

    [WindowsStorageFact]
    public void Redirected_ancestors_and_deletion_capable_untrusted_parents_are_refused()
    {
        var link = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(link, _root);
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Path.Combine(link, "journal"), Checkpoint));
        GrantEveryone(_root, directory: true, FileSystemRights.DeleteSubdirectoriesAndFiles);
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        Assert.False(Directory.Exists(Journal));
    }

    [WindowsStorageFact]
    public void Append_only_rights_on_a_parent_cannot_redirect_pinned_components()
    {
        // Windows splits POSIX directory write into distinct rights. System-drive
        // roots grant create-directories/append to all users; exclusive creation,
        // protected leaf ACLs and retained handles keep that from redirecting us.
        GrantEveryone(_root, directory: true, FileSystemRights.CreateDirectories);
        using var storage = WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        storage.ValidateBinding();
    }

    [WindowsStorageTheory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    public void Mutation_capable_rights_on_a_parent_are_refused(FileSystemRights rights)
    {
        // Write-data or write-attributes can set reparse data on an empty
        // directory in place; the deletion class can remove or rebind
        // components outright. Both must refuse provisioning.
        GrantEveryone(_root, directory: true, rights);
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        Assert.False(Directory.Exists(Journal));
    }

    [WindowsStorageTheory]
    [InlineData("journal")]
    [InlineData("checkpoint")]
    public void A_held_directory_cannot_be_moved_or_replaced_while_the_storage_is_open(string changed)
    {
        using var storage = WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        var path = changed == "journal" ? Journal : Checkpoint;
        Assert.ThrowsAny<IOException>(() => Directory.Move(path, path + "-original"));
        storage.ValidateBinding();
        Assert.True(File.Exists(changed == "journal" ? LedgerFile : CheckpointFile));
    }

    [WindowsStorageTheory]
    [InlineData("checkpoint:temp-created", 1, false)]
    [InlineData("checkpoint:file-synced", 1, false)]
    [InlineData("checkpoint:renamed", 1, false)]
    [InlineData("checkpoint:rename-flushed", 1, false)]
    [InlineData("checkpoint:directory-flushed", 1, false)]
    [InlineData("ledger:temp-created", 1, false)]
    [InlineData("ledger:file-synced", 1, false)]
    [InlineData("ledger:renamed", 1, true)]
    [InlineData("ledger:rename-flushed", 1, true)]
    [InlineData("ledger:directory-flushed", 1, true)]
    [InlineData("checkpoint:temp-created", 2, true)]
    [InlineData("checkpoint:file-synced", 2, true)]
    [InlineData("checkpoint:renamed", 2, true)]
    [InlineData("checkpoint:rename-flushed", 2, true)]
    [InlineData("checkpoint:directory-flushed", 2, true)]
    public async Task Lost_acknowledgement_at_each_real_write_boundary_poison_fences_until_exact_reopening(
        string phase, int occurrence, bool newVisible)
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        // NativeWorkJournal.OpenExisting itself reconfirms the checkpoint. Arm only after opening.
        var armed = false; var matches = 0;
        var storage = WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint, value => {
            if (armed && value == phase && ++matches == occurrence) throw new IOException("injected lost acknowledgement");
        });
        var journal = NativeWorkJournal.OpenExisting(storage); armed = true;
        await Assert.ThrowsAsync<IOException>(() => Commit(journal));
        Assert.True(journal.IsPoisoned); await journal.DisposeAsync();
        await using var reopened = Open();
        Assert.Equal(newVisible ? 1 : 0, reopened.Snapshot.Revision);
        Assert.Null(NativeWorkCheckpointProtocol.Decode(File.ReadAllBytes(CheckpointFile)).Pending);
    }

    [WindowsStorageTheory]
    [InlineData("journal:created", false)]
    [InlineData("journal:parent-flushed", false)]
    [InlineData("checkpoint-root:created", false)]
    [InlineData("checkpoint-root:parent-flushed", false)]
    [InlineData("lock:created", false)]
    [InlineData("lock:file-synced", false)]
    [InlineData("lock:directory-flushed", false)]
    [InlineData("checkpoint:created", false)]
    [InlineData("checkpoint:file-synced", false)]
    [InlineData("checkpoint:directory-flushed", false)]
    [InlineData("ledger:created", false)]
    [InlineData("ledger:file-synced", true)]
    [InlineData("ledger:directory-flushed", true)]
    public async Task Bootstrap_never_repairs_incomplete_authority_after_an_interruption(string phase, bool completeVisible)
    {
        var fired = false;
        var thrown = Assert.Throws<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint, value => {
            if (value == phase) { fired = true; throw new IOException("bootstrap interrupted"); }
        }));
        Assert.True(fired, $"nominated boundary {phase} was never reached");
        Assert.Equal("bootstrap interrupted", thrown.Message);
        if (completeVisible) { await using var journal = Open(); Assert.Equal(0, journal.Snapshot.Revision); }
        else
        {
            var error = Record.Exception(() => Open());
            Assert.True(error is IOException or InvalidDataException, error?.ToString());
        }
        Assert.ThrowsAny<IOException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
    }

    [WindowsStorageFact]
    public void A_recreated_lock_cannot_keep_the_old_checkpoints_authority_even_with_identical_header_bytes()
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var header = File.ReadAllBytes(LockFile);
        var storage = WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint);
        // The held coordinator is opened without delete sharing, so the named lock cannot
        // be removed or swapped while this storage is alive.
        Assert.ThrowsAny<IOException>(() => File.Delete(LockFile));
        storage.ValidateBinding(); storage.Dispose();
        File.Delete(LockFile);
        File.WriteAllBytes(LockFile, header);
        var replacement = new FileInfo(LockFile); var security = replacement.GetAccessControl();
        // Administrator-created files default to the Administrators group as
        // owner; storage requires the creating account, so set it explicitly.
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            security.RemoveAccessRule(rule);
        foreach (var sid in Trusted())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        replacement.SetAccessControl(security);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [WindowsStorageFact]
    public async Task A_reconfirming_storage_keeps_the_named_ledger_exclusively_held()
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var fired = false;
        var storage = WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint, phase => {
            if (phase != "ledger:reconfirm-file-synced") return;
            fired = true;
            // Unlike Linux, the named ledger cannot be unlinked beneath the open
            // no-sharing handle, so a detached predecessor cannot be substituted.
            Assert.ThrowsAny<IOException>(() => File.Move(LedgerFile, LedgerFile + ".original"));
        });
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        Assert.True(fired, "reconfirm boundary was never reached");
        Assert.Equal(0, journal.Snapshot.Revision);
    }

    [WindowsStorageFact]
    public void Reconfirming_a_substituted_ledger_with_identical_bytes_is_refused()
    {
        WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var fired = false;
        var storage = WindowsNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint, phase => {
            if (phase != "ledger:reconfirm-directory-flushed") return;
            fired = true;
            // The writable handle is closed by now; an owner process can swap in a
            // byte-identical new file. The named-entry identity recheck must refuse.
            var bytes = File.ReadAllBytes(LedgerFile);
            var original = new FileInfo(LedgerFile).GetAccessControl();
            File.Move(LedgerFile, LedgerFile + ".original");
            File.WriteAllBytes(LedgerFile, bytes);
            var replacement = new FileInfo(LedgerFile);
            replacement.SetAccessControl(original);
            var secured = replacement.GetAccessControl();
            secured.SetOwner(WindowsIdentity.GetCurrent().User!);
            replacement.SetAccessControl(secured);
        });
        Assert.ThrowsAny<IOException>(() => NativeWorkJournal.OpenExisting(storage));
        Assert.True(fired, "reconfirm boundary was never reached");
    }

    [WindowsStorageFact]
    public void Coordinator_and_directory_handles_are_not_inheritable()
    {
        using var storage = WindowsNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        var coordinator = (FileStream)typeof(WindowsNativeWorkJournalStorage)
            .GetField("_coordinator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(storage)!;
        Assert.True(GetHandleInformation(coordinator.SafeFileHandle, out var flags));
        Assert.Equal(0u, flags & 1u);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "GetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out uint flags);
}

public sealed class WindowsStorageFactAttribute : FactAttribute
{
    public WindowsStorageFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows NTFS storage and handle ownership."; }
}
public sealed class WindowsStorageTheoryAttribute : TheoryAttribute
{
    public WindowsStorageTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows NTFS storage and handle ownership."; }
}

public sealed class WindowsNativeJournalStoragePlatformTests
{
    // The storage type is Windows-annotated; its own platform gate must throw
    // before any Windows API is touched, which is what this test exercises.
    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_non_windows_host_cannot_start_windows_accounting()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => WindowsNativeWorkJournalStorage.ProvisionNew(@"C:\missing\journal", @"C:\missing\authority"));
    }
}
