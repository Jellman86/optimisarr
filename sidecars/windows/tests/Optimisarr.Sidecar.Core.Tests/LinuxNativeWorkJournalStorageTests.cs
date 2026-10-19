using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxNativeWorkJournalStorageTests : IDisposable
{
    // Quark's /tmp is memory backed. Fixture storage must obey the same persistence policy
    // as the adapter, so use an owned directory on the checkout's filesystem.
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), "native-journal-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "journal");
    private string Checkpoint => Path.Combine(_root, "authority");
    private string LedgerFile => Path.Combine(Journal, "ledger.json");
    private string CheckpointFile => Path.Combine(Checkpoint, "checkpoint.json");
    private string LockFile => Path.Combine(Journal, "coordinator.lock");
    public LinuxNativeWorkJournalStorageTests() => Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    private NativeWorkJournal Open() => NativeWorkJournal.OpenExisting(LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint));
    private static NativeWorkSnapshot Add(NativeWorkSnapshot snapshot, string root) => snapshot with {
        Revision = snapshot.Revision + 1,
        Obligations = [new(Guid.NewGuid(), NativeWorkPurpose.Capability, null, null, root,
            new("root:1", "control:1", "gate:1"), "volume:1", 100, 200, false, NativeWorkPhase.Intent)] };
    private Task<int> Commit(NativeWorkJournal journal) => journal.ExecuteAsync<int>((scope, _) => {
        scope.Commit(Add(scope.Snapshot, _root)); return Task.CompletedTask;
    }, _ => Task.FromResult(42), (_, _) => Task.CompletedTask);

    [LinuxStorageFact]
    public async Task Provisioning_creates_private_independent_authority_and_restart_retains_exact_debt()
    {
        var storage = LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        var binding = storage.Binding;
        await using (var journal = NativeWorkJournal.OpenExisting(storage))
        {
            Assert.Equal(0, journal.Snapshot.Revision);
            await Commit(journal);
            Assert.Equal(1, journal.Snapshot.Revision);
        }
        using var reopened = LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint);
        Assert.Equal(binding, reopened.Binding);
        var restored = NativeWorkLedger.Decode(reopened.ReadLedger());
        Assert.Equal(1, restored.Revision); Assert.Single(restored.Obligations);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Journal));
        Assert.Equal(File.GetUnixFileMode(Journal), File.GetUnixFileMode(Checkpoint));
        foreach (var path in new[] { LedgerFile, CheckpointFile, LockFile })
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [LinuxStorageFact]
    public async Task A_second_owner_cannot_open_or_provision_until_the_stable_lock_is_released()
    {
        var first = NativeWorkJournal.OpenExisting(LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        var before = File.ReadAllBytes(LedgerFile);
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint));
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        Assert.Equal(before, File.ReadAllBytes(LedgerFile));
        await first.DisposeAsync();
        await using var second = Open();
        Assert.Equal(0, second.Snapshot.Revision);
    }

    [LinuxStorageTheory]
    [InlineData("ledger")]
    [InlineData("checkpoint")]
    [InlineData("lock")]
    public async Task Missing_metadata_cannot_reinitialize_even_an_empty_existing_store(string missing)
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var file = missing switch { "ledger" => LedgerFile, "checkpoint" => CheckpointFile, _ => LockFile };
        File.Delete(file);
        var error = await Record.ExceptionAsync(async () => { await using var _ = Open(); });
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.False(File.Exists(file));
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
    }

    [LinuxStorageFact]
    public async Task An_older_empty_ledger_cannot_hide_independently_committed_debt()
    {
        var journal = NativeWorkJournal.OpenExisting(LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        var empty = File.ReadAllBytes(LedgerFile);
        await Commit(journal); await journal.DisposeAsync();
        File.WriteAllBytes(LedgerFile, empty);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [LinuxStorageTheory]
    [InlineData("journal")]
    [InlineData("checkpoint")]
    [InlineData("lock")]
    [InlineData("ledger")]
    [InlineData("authority-file")]
    public void Widened_private_permissions_refuse_accounting(string changed)
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = changed switch { "journal" => Journal, "checkpoint" => Checkpoint, "lock" => LockFile,
            "ledger" => LedgerFile, _ => CheckpointFile };
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.GroupRead);
        Assert.ThrowsAny<IOException>(() => Open());
    }

    [LinuxStorageTheory]
    [InlineData("ledger")]
    [InlineData("checkpoint")]
    [InlineData("lock")]
    public void Redirected_or_shared_files_cannot_supply_or_receive_authority(string changed)
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = changed switch { "ledger" => LedgerFile, "checkpoint" => CheckpointFile, _ => LockFile };
        var saved = File.ReadAllBytes(path); var victim = Path.Combine(_root, "sentinel"); File.WriteAllBytes(victim, saved);
        File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(path); File.CreateSymbolicLink(path, victim);
        Assert.ThrowsAny<IOException>(() => Open()); Assert.Equal(saved, File.ReadAllBytes(victim));
        File.Delete(path); Assert.Equal(0, Link(victim, path));
        Assert.ThrowsAny<IOException>(() => Open()); Assert.Equal(saved, File.ReadAllBytes(victim));
    }

    [LinuxStorageFact]
    public void A_fifo_is_refused_without_waiting_for_a_writer()
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        File.Delete(LedgerFile); Assert.Equal(0, MkFifo(LedgerFile, 0x180));
        Assert.ThrowsAny<IOException>(() => Open());
    }

    [LinuxStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public void Oversized_or_corrupt_authority_is_refused_without_an_empty_fallback(bool checkpoint)
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var path = checkpoint ? CheckpointFile : LedgerFile;
        File.WriteAllBytes(path, new byte[(checkpoint ? NativeWorkCheckpointProtocol.MaximumBytes : NativeWorkLedger.MaximumBytes) + 1]);
        Assert.ThrowsAny<IOException>(() => Open());
    }

    [LinuxStorageFact]
    public void Journal_and_checkpoint_directories_must_be_distinct_and_nonoverlapping()
    {
        Assert.Throws<ArgumentException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Journal));
        Assert.Throws<ArgumentException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Path.Combine(Journal, "authority")));
        Assert.Throws<ArgumentException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Path.Combine(Checkpoint, "journal"), Checkpoint));
        Assert.False(Directory.Exists(Journal)); Assert.False(Directory.Exists(Checkpoint));
    }

    [LinuxStorageFact]
    public void Redirected_ancestors_and_writable_untrusted_parents_are_refused()
    {
        var link = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(link, _root);
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Path.Combine(link, "journal"), Checkpoint));
        File.SetUnixFileMode(_root, File.GetUnixFileMode(_root) | UnixFileMode.GroupWrite);
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
        Assert.False(Directory.Exists(Journal));
    }

    [LinuxStorageTheory]
    [InlineData("journal")]
    [InlineData("checkpoint")]
    public void A_rebound_directory_is_detected_and_cannot_redirect_a_write(string changed)
    {
        using var storage = LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        var path = changed == "journal" ? Journal : Checkpoint;
        var old = path + "-original"; Directory.Move(path, old); Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sentinel = Path.Combine(path, changed == "journal" ? "ledger.json" : "checkpoint.json");
        File.WriteAllText(sentinel, "unowned sentinel");
        Assert.ThrowsAny<IOException>(() => storage.ValidateBinding());
        Assert.ThrowsAny<IOException>(() => storage.ReplaceLedger(NativeWorkLedger.Encode(new(1, Guid.NewGuid(), 0, []))));
        Assert.Equal("unowned sentinel", File.ReadAllText(sentinel));
    }

    [LinuxStorageTheory]
    [InlineData("checkpoint:temp-created", 1, false)]
    [InlineData("checkpoint:file-synced", 1, false)]
    [InlineData("checkpoint:renamed", 1, false)]
    [InlineData("checkpoint:directory-synced", 1, false)]
    [InlineData("ledger:temp-created", 1, false)]
    [InlineData("ledger:file-synced", 1, false)]
    [InlineData("ledger:renamed", 1, true)]
    [InlineData("ledger:directory-synced", 1, true)]
    [InlineData("checkpoint:temp-created", 2, true)]
    [InlineData("checkpoint:file-synced", 2, true)]
    [InlineData("checkpoint:renamed", 2, true)]
    [InlineData("checkpoint:directory-synced", 2, true)]
    public async Task Lost_acknowledgement_at_each_real_write_boundary_poison_fences_until_exact_reopening(
        string phase, int occurrence, bool newVisible)
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        // NativeWorkJournal.OpenExisting itself reconfirms the checkpoint. Arm only after opening.
        var armed = false; var matches = 0;
        var storage = LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint, value => {
            if (armed && value == phase && ++matches == occurrence) throw new IOException("injected lost acknowledgement");
        });
        var journal = NativeWorkJournal.OpenExisting(storage); armed = true;
        await Assert.ThrowsAsync<IOException>(() => Commit(journal));
        Assert.True(journal.IsPoisoned); await journal.DisposeAsync();
        await using var reopened = Open();
        Assert.Equal(newVisible ? 1 : 0, reopened.Snapshot.Revision);
        Assert.Null(NativeWorkCheckpointProtocol.Decode(File.ReadAllBytes(CheckpointFile)).Pending);
    }

    [LinuxStorageTheory]
    [InlineData("journal:created", false)]
    [InlineData("journal:parent-synced", false)]
    [InlineData("checkpoint-root:created", false)]
    [InlineData("checkpoint-root:parent-synced", false)]
    [InlineData("lock:created", false)]
    [InlineData("lock:file-synced", false)]
    [InlineData("lock:directory-synced", false)]
    [InlineData("checkpoint:created", false)]
    [InlineData("checkpoint:file-synced", false)]
    [InlineData("checkpoint:directory-synced", false)]
    [InlineData("ledger:created", false)]
    [InlineData("ledger:file-synced", true)]
    [InlineData("ledger:directory-synced", true)]
    public async Task Bootstrap_never_repairs_incomplete_authority_after_an_interruption(string phase, bool completeVisible)
    {
        Assert.Throws<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint, value => {
            if (value == phase) throw new IOException("bootstrap interrupted");
        }));
        if (completeVisible) { await using var journal = Open(); Assert.Equal(0, journal.Snapshot.Revision); }
        else
        {
            var error = Record.Exception(() => Open());
            Assert.True(error is IOException or InvalidDataException, error?.ToString());
        }
        Assert.ThrowsAny<IOException>(() => LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint));
    }

    [LinuxStorageFact]
    public void The_stable_lock_and_private_directory_descriptors_are_close_on_exec()
    {
        using var storage = LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint);
        foreach (var path in new[] { Journal, Checkpoint, LockFile })
        {
            var descriptors = Directory.EnumerateFileSystemEntries("/proc/self/fd")
                .Where(name => new FileInfo(name).LinkTarget == path).ToArray();
            Assert.NotEmpty(descriptors);
            foreach (var descriptor in descriptors) Assert.Equal(1, Fcntl(int.Parse(Path.GetFileName(descriptor)), 1) & 1);
        }
    }

    [LinuxStorageFact]
    public void A_recreated_lock_cannot_keep_the_old_checkpoints_authority_even_with_identical_header_bytes()
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        // Managed reads take a sharing lock that the held coordinator flock refuses;
        // capture the header while unowned, then reopen so recreation races a live owner.
        var header = File.ReadAllBytes(LockFile);
        var storage = LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint);
        File.Delete(LockFile); File.WriteAllBytes(LockFile, header);
        File.SetUnixFileMode(LockFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.ThrowsAny<IOException>(() => storage.ValidateBinding()); storage.Dispose();
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [LinuxStorageFact]
    public void Reconfirming_an_unlinked_predecessor_cannot_confirm_the_visible_ledger_generation()
    {
        LinuxNativeWorkJournalStorage.ProvisionNew(Journal, Checkpoint).Dispose();
        var storage = LinuxNativeWorkJournalStorage.OpenExisting(Journal, Checkpoint, phase => {
            if (phase != "ledger:reconfirm-file-synced") return;
            var bytes = File.ReadAllBytes(LedgerFile);
            File.Move(LedgerFile, LedgerFile + ".original"); File.WriteAllBytes(LedgerFile, bytes);
            File.SetUnixFileMode(LedgerFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        });
        Assert.ThrowsAny<IOException>(() => NativeWorkJournal.OpenExisting(storage));
    }

    // statfs f_type magics, as in the adapter's persistence allowlist.
    private const long Ext4 = 0xef53, Btrfs = 0x9123683e, Xfs = 0x58465342;

    [Theory]
    [InlineData(Btrfs)]
    [InlineData(Ext4)]
    public void The_persisted_identity_key_survives_a_remounts_new_anonymous_device_and_mount_ids(long filesystem)
    {
        // Btrfs assigns anonymous st_dev numbers per mount (observed 0:51 -> 0:34 -> 0:51 over
        // three guest boots) and mnt_id is per-mount too, so neither may reach the persisted
        // binding on filesystems whose f_fsid is UUID-derived. The volume's f_fsid and the
        // inode were stable across every boot.
        var provisioned = new LinuxNativeWorkJournalStorage.Identity(
            FileSystemId: 0xe804917c8989937f, FileSystemType: filesystem, Inode: 265, Major: 0, Minor: 51, Mount: 23);
        var afterReboot = provisioned with { Minor = 34, Mount = 41 };

        Assert.Equal(provisioned.Key, afterReboot.Key);
        // Same-process validation stays strict: only the persisted key forgives a remount.
        Assert.NotEqual(provisioned, afterReboot);
    }

    [Fact]
    public void The_persisted_identity_key_stays_device_bound_where_f_fsid_is_device_derived()
    {
        // XFS derives f_fsid from the device number, so it cannot prove remount stability; its
        // key keeps the device identity and a changed device fails closed, as before.
        var provisioned = new LinuxNativeWorkJournalStorage.Identity(
            FileSystemId: 0xfc01, FileSystemType: Xfs, Inode: 265, Major: 252, Minor: 1, Mount: 23);

        Assert.NotEqual(provisioned.Key, (provisioned with { Minor = 2 }).Key);
        Assert.Equal(provisioned.Key, (provisioned with { Mount = 41 }).Key);
    }

    [Fact]
    public void The_persisted_identity_key_distinguishes_filesystems_and_inodes()
    {
        var identity = new LinuxNativeWorkJournalStorage.Identity(
            FileSystemId: 0xe804917c8989937f, FileSystemType: Btrfs, Inode: 265, Major: 0, Minor: 51, Mount: 23);

        Assert.NotEqual(identity.Key, (identity with { FileSystemId = 0xe804917c8989937e }).Key);
        Assert.NotEqual(identity.Key, (identity with { Inode = 266 }).Key);
        Assert.NotEqual(identity.Key, (identity with { FileSystemType = Ext4 }).Key);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo(string path, uint mode);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int descriptor, int command);
}

public sealed class LinuxStorageFactAttribute : FactAttribute
{
    public LinuxStorageFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires native Linux persistent storage and file-descriptor ownership."; }
}
public sealed class LinuxStorageTheoryAttribute : TheoryAttribute
{
    public LinuxStorageTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires native Linux persistent storage and file-descriptor ownership."; }
}

public sealed class NativeJournalStoragePlatformTests
{
    [Fact]
    public void A_non_linux_host_cannot_start_linux_accounting()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.Throws<PlatformNotSupportedException>(() => LinuxNativeWorkJournalStorage.ProvisionNew("/missing/journal", "/missing/authority"));
    }
}
