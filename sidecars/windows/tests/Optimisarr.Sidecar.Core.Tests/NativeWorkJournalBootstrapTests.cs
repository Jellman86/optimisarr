using System.Runtime.Versioning;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeWorkJournalBootstrapRestoreTests
{
    private static PhysicalVolumeId Volume(string value = "device:one") => new(value);
    private static NativeWorkObligation Obligation(
        NativeWorkPurpose purpose,
        NativeWorkPhase phase,
        string volume = "device:one",
        long nativeMemory = 100,
        long scratch = 200,
        bool scratchUsesMemory = false,
        Guid? owner = null) => new(
            owner ?? Guid.NewGuid(),
            purpose,
            purpose == NativeWorkPurpose.Job ? 42 : null,
            purpose == NativeWorkPurpose.Job ? Guid.NewGuid() : null,
            OperatingSystem.IsWindows() ? @"C:\owned" : "/owned",
            new("root:1", "control:1", "gate:1"),
            volume,
            nativeMemory,
            scratch,
            scratchUsesMemory,
            phase);
    private static NativeWorkSnapshot Snapshot(params NativeWorkObligation[] obligations) =>
        new(1, Guid.NewGuid(), 0, [.. obligations]);

    [Fact]
    public void Restoration_charges_every_unreleased_phase_and_purpose_to_the_exact_physical_volume()
    {
        var intent = Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent);
        var authorized = Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Authorized,
            "device:two", nativeMemory: 50, scratch: 25, scratchUsesMemory: true);
        var retired = Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Retired,
            nativeMemory: 10, scratch: 5);
        var released = Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Released,
            "device:two", nativeMemory: 900, scratch: 900, scratchUsesMemory: true);

        var pool = NativeWorkJournalBootstrap.RestoreCharges(Snapshot(intent, authorized, retired, released));

        Assert.Equal((795L, 815L), pool.Available(Volume(), 1000, 1000));
        Assert.Equal((975L, 815L), pool.Available(Volume("device:two"), 1000, 1000));
        Assert.Equal((0L, 0L), pool.Available(Volume(), 100, 100));
        Assert.Null(pool.TryAcquire(Volume(), 796, 1, 1000, 1000));
    }

    [Fact]
    public void Restoration_rejects_an_invalid_snapshot_before_mutating_the_staging_pool()
    {
        var owner = Guid.NewGuid();
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var snapshot = Snapshot(
            Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent, owner: owner),
            Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Intent, owner: owner));

        Assert.Throws<InvalidDataException>(() => NativeWorkJournalBootstrap.RestoreCharges(snapshot, pool));
        Assert.Equal((1000L, 1000L), pool.Available(Volume(), 1000, 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("device:\0other")]
    public void Restoration_rejects_invalid_physical_identities(string volume)
    {
        var snapshot = Snapshot(Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent, volume));
        Assert.Throws<InvalidDataException>(() => NativeWorkJournalBootstrap.RestoreCharges(snapshot));
    }

    [Fact]
    public void Restoration_rejects_checked_charge_overflow_without_publishing_a_pool()
    {
        var snapshot = Snapshot(
            Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent, nativeMemory: long.MaxValue, scratch: 1),
            Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Authorized, nativeMemory: 1, scratch: 1));

        Assert.Throws<InvalidDataException>(() => NativeWorkJournalBootstrap.RestoreCharges(snapshot));
    }

    [Fact]
    public void Restoration_into_a_conflicting_pool_fails_closed()
    {
        var owner = Guid.NewGuid();
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        Assert.True(pool.TryRestore(owner, Volume(), 200, 100));
        var snapshot = Snapshot(
            Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Intent, nativeMemory: 25, scratch: 10),
            Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Authorized, nativeMemory: 101, owner: owner));

        Assert.Throws<InvalidDataException>(() => NativeWorkJournalBootstrap.RestoreCharges(snapshot, pool));
        Assert.Equal((790L, 875L), pool.Available(Volume(), 1000, 1000));
    }
}

[SupportedOSPlatform("linux")]
public sealed class NativeWorkJournalBootstrapLinuxTests : IDisposable
{
    // The Linux adapter refuses memory-backed storage, so keep fixtures on disk.
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), "native-bootstrap-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "journal");
    private string Checkpoint => Path.Combine(_root, "authority");
    private string LedgerFile => Path.Combine(Journal, "ledger.json");
    private string CheckpointFile => Path.Combine(Checkpoint, "checkpoint.json");
    private string LockFile => Path.Combine(Journal, "coordinator.lock");
    public NativeWorkJournalBootstrapLinuxTests() =>
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static NativeWorkObligation Obligation(
        NativeWorkPurpose purpose,
        NativeWorkPhase phase,
        string root,
        string volume = "device:one",
        long nativeMemory = 100,
        long scratch = 200,
        bool scratchUsesMemory = false) => new(
            Guid.NewGuid(), purpose,
            purpose == NativeWorkPurpose.Job ? 42 : null,
            purpose == NativeWorkPurpose.Job ? Guid.NewGuid() : null,
            root, new("root:1", "control:1", "gate:1"), volume, nativeMemory, scratch, scratchUsesMemory, phase);

    private static async Task Commit(NativeWorkJournal journal, Func<NativeWorkSnapshot, NativeWorkSnapshot> next) =>
        await journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(next(scope.Snapshot)); return Task.CompletedTask;
        }, _ => Task.FromResult(42), (_, _) => Task.CompletedTask);

    [LinuxStorageFact]
    public async Task Explicit_provisioning_then_restart_restores_the_exact_remaining_budget()
    {
        var intent = Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent, _root);
        var authorized = Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Intent, _root,
            "device:two", nativeMemory: 50, scratch: 25, scratchUsesMemory: true);
        var retired = Obligation(NativeWorkPurpose.Capability, NativeWorkPhase.Intent, _root,
            nativeMemory: 10, scratch: 5);
        var released = Obligation(NativeWorkPurpose.Job, NativeWorkPhase.Intent, _root,
            "device:two", nativeMemory: 900, scratch: 900, scratchUsesMemory: true);

        await using (var provisioned = await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint))
        {
            Assert.Equal(0, provisioned.Journal.Snapshot.Revision);
            await Commit(provisioned.Journal, snapshot => snapshot with {
                Revision = snapshot.Revision + 1, Obligations = [intent, authorized, retired, released] });
            await Commit(provisioned.Journal, snapshot => snapshot with {
                Revision = snapshot.Revision + 1,
                Obligations = [intent, authorized with { Phase = NativeWorkPhase.Authorized },
                    retired with { Phase = NativeWorkPhase.Retired }, released with { Phase = NativeWorkPhase.Retired }] });
            await Commit(provisioned.Journal, snapshot => snapshot with {
                Revision = snapshot.Revision + 1,
                Obligations = [intent, authorized with { Phase = NativeWorkPhase.Authorized },
                    retired with { Phase = NativeWorkPhase.Retired }, released with { Phase = NativeWorkPhase.Released }] });
        }

        await using var restored = await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint);
        Assert.Equal(3, restored.Journal.Snapshot.Revision);
        Assert.Equal((795L, 815L), restored.ResourcePool.Available(new PhysicalVolumeId("device:one"), 1000, 1000));
        Assert.Equal((975L, 815L), restored.ResourcePool.Available(new PhysicalVolumeId("device:two"), 1000, 1000));
    }

    [LinuxStorageFact]
    public async Task Normal_open_refuses_a_missing_store_without_creating_it()
    {
        var error = await Record.ExceptionAsync(async () => { await using var _ =
            await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint); });
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.False(Directory.Exists(Journal));
        Assert.False(Directory.Exists(Checkpoint));
    }

    [LinuxStorageTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Normal_open_and_reprovisioning_refuse_partial_stores(bool journalExists, bool authorityExists)
    {
        await using (await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint)) { }
        Directory.Delete(journalExists ? Checkpoint : Journal, recursive: true);

        var openError = await Record.ExceptionAsync(async () => { await using var _ =
            await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint); });
        Assert.True(openError is IOException or InvalidDataException, openError?.ToString());
        var provisionError = await Record.ExceptionAsync(async () => { await using var _ =
            await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint); });
        Assert.True(provisionError is IOException or InvalidDataException, provisionError?.ToString());
        // Failed provisioning deliberately retains the newly created journal side
        // when only authority survived; it never converts partial state into authority.
        Assert.True(Directory.Exists(Journal));
        Assert.Equal(authorityExists, Directory.Exists(Checkpoint));
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
    public async Task Explicit_provisioning_never_repairs_its_own_interruption(string phase, bool completeVisible)
    {
        var fired = false;
        var thrown = await Assert.ThrowsAsync<IOException>(async () => {
            await using var _ = await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint, value => {
                if (value == phase) { fired = true; throw new IOException("bootstrap interrupted"); }
            });
        });
        Assert.True(fired, $"nominated boundary {phase} was never reached");
        Assert.Equal("bootstrap interrupted", thrown.Message);
        if (completeVisible)
        {
            await using var restored = await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint);
            Assert.Equal(0, restored.Journal.Snapshot.Revision);
        }
        else
        {
            var error = await Record.ExceptionAsync(async () => { await using var _ =
                await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint); });
            Assert.True(error is IOException or InvalidDataException, error?.ToString());
        }
        var repro = await Record.ExceptionAsync(async () => { await using var _ =
            await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint); });
        Assert.True(repro is IOException or InvalidDataException, repro?.ToString());
    }

    [LinuxStorageFact]
    public async Task Concurrent_explicit_provisioning_has_exactly_one_owner()
    {
        using var start = new ManualResetEventSlim();
        var outcomes = new NativeWorkRestoredJournal?[2];
        var errors = new Exception?[2];
        var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(async () => {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            try { outcomes[index] = await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint); }
            catch (Exception error) { errors[index] = error; }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        var winner = Assert.Single(outcomes, outcome => outcome is not null)!;
        await using (winner)
        {
            var loser = Assert.Single(errors, error => error is not null);
            Assert.True(loser is IOException or InvalidDataException);
            Assert.Equal(0, winner.Journal.Snapshot.Revision);
            var second = await Record.ExceptionAsync(async () => { await using var _ =
                await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint); });
            Assert.True(second is IOException or InvalidDataException, second?.ToString());
        }
    }

    [LinuxStorageFact]
    public async Task A_corrupt_store_refusal_preserves_all_existing_bytes()
    {
        await using (await NativeWorkJournalBootstrap.ProvisionNewAsync(Journal, Checkpoint)) { }
        var checkpoint = File.ReadAllBytes(CheckpointFile);
        checkpoint[^2] ^= 0x20;
        File.WriteAllBytes(CheckpointFile, checkpoint);
        var before = new[] { LedgerFile, CheckpointFile, LockFile }
            .ToDictionary(path => path, File.ReadAllBytes);

        var error = await Record.ExceptionAsync(async () => { await using var _ =
            await NativeWorkJournalBootstrap.OpenExistingAsync(Journal, Checkpoint); });

        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

public sealed class NativeWorkJournalBootstrapPlatformTests
{
    [Fact]
    public async Task A_non_linux_host_is_refused_before_any_filesystem_creation()
    {
        if (OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "native-bootstrap-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(root, "journal");
        var checkpoint = Path.Combine(root, "authority");

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => NativeWorkJournalBootstrap.ProvisionNewAsync(journal, checkpoint));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => NativeWorkJournalBootstrap.OpenExistingAsync(journal, checkpoint));
        Assert.False(Directory.Exists(root));
    }
}
