namespace Optimisarr.Sidecar.Linux;

/// <summary>Prevents separate identities from deleting each other's job directories.</summary>
public sealed class WorkerDirectoryLock : IDisposable
{
    private readonly FileStream _lock;

    public WorkerDirectoryLock(string directory)
    {
        Directory.CreateDirectory(directory);
        _lock = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _lock.Dispose();
}
