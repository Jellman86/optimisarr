using System.Runtime.InteropServices;
using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Owns a journal and the physical pool restored from its exact snapshot.</summary>
internal sealed class NativeWorkRestoredJournal(
    NativeWorkJournal journal,
    PerceptualJobResourcePool resourcePool) : IAsyncDisposable
{
    public NativeWorkJournal Journal { get; } = journal ?? throw new ArgumentNullException(nameof(journal));
    public PerceptualJobResourcePool ResourcePool { get; } = resourcePool ?? throw new ArgumentNullException(nameof(resourcePool));

    public ValueTask DisposeAsync() => Journal.DisposeAsync();
}

/// <summary>
/// Unconnected Linux bootstrap foundation. It deliberately has no service or admission
/// callers. Normal opening never provisions or repairs a store; provisioning is a
/// separate, explicit operator/install action. Windows remains an unqualified storage
/// candidate and Darwin has no .NET adapter, so both are refused before filesystem access.
/// </summary>
internal static class NativeWorkJournalBootstrap
{
    /// <summary>
    /// Create positively new authority. This is not a service-startup fallback: a future
    /// installer/operator path must authorize the intended installation and paths first.
    /// Failed provisioning may retain the partial private store for explicit recovery.
    /// </summary>
    public static async Task<NativeWorkRestoredJournal> ProvisionNewAsync(
        string journalPath, string checkpointPath, Action<string>? storageStep = null)
    {
        RequireSupportedLinux();
        return await OpenStorageAsync(
            LinuxNativeWorkJournalStorage.ProvisionNew(journalPath, checkpointPath, storageStep))
            .ConfigureAwait(false);
    }

    /// <summary>Open an existing store and fully restore its unreleased physical charges.</summary>
    public static async Task<NativeWorkRestoredJournal> OpenExistingAsync(
        string journalPath, string checkpointPath, Action<string>? storageStep = null)
    {
        RequireSupportedLinux();
        return await OpenStorageAsync(
            LinuxNativeWorkJournalStorage.OpenExisting(journalPath, checkpointPath, storageStep))
            .ConfigureAwait(false);
    }

    internal static async Task<NativeWorkRestoredJournal> OpenStorageAsync(INativeWorkJournalStorage storage)
    {
        NativeWorkJournal journal = null!;
        try
        {
            journal = NativeWorkJournal.OpenExisting(storage);
            var pool = RestoreCharges(journal.Snapshot);
            return new NativeWorkRestoredJournal(journal, pool);
        }
        catch
        {
            if (journal is not null) await journal.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static PerceptualJobResourcePool RestoreCharges(NativeWorkSnapshot snapshot)
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        RestoreCharges(snapshot, pool);
        return pool;
    }

    // A caller-supplied pool may be partially restored when this fails. Production
    // bootstrap passes only an unpublished staging pool and publishes it after success.
    internal static void RestoreCharges(NativeWorkSnapshot snapshot, PerceptualJobResourcePool resourcePool)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resourcePool);
        // Reuse the ledger's canonical validation, including owner uniqueness,
        // charge bounds and checked aggregate overflow, before interpreting charges.
        _ = NativeWorkLedger.Encode(snapshot);

        List<(Guid Owner, PhysicalVolumeId Volume, long Disk, long Memory)> charges = [];
        foreach (var obligation in snapshot.Obligations)
        {
            if (!obligation.HoldsResources) continue;
            PhysicalVolumeId volume;
            long memory;
            try
            {
                volume = new PhysicalVolumeId(obligation.VolumeIdentity);
                memory = obligation.MemoryCharge;
            }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            {
                throw new InvalidDataException("Native work accounting contains an unrestorable charge.", error);
            }
            charges.Add((obligation.Owner, volume, obligation.DiskCharge, memory));
        }

        foreach (var charge in charges)
        {
            if (!resourcePool.TryRestore(charge.Owner, charge.Volume, charge.Disk, charge.Memory))
                throw new InvalidDataException("Native work accounting could not be completely restored.");
        }
    }

    private static void RequireSupportedLinux()
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Native work journal bootstrap currently requires a supported 64-bit Linux host.");
    }
}
