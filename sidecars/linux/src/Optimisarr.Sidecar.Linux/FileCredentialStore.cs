using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Linux;

/// <summary>Private, atomic pairing state on the operator's persistent config volume.</summary>
public sealed class FileCredentialStore(string directory) : ICredentialStore
{
    private string PathName => Path.Combine(directory, "pairing.json");

    public StoredPairing? Load()
    {
        if (!File.Exists(PathName)) return null;
        if (File.GetAttributes(PathName).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Pairing state must be a regular file.");
        return JsonSerializer.Deserialize<StoredPairing>(File.ReadAllText(PathName))
            ?? throw new IOException("Pairing state is empty or invalid.");
    }

    public void Save(StoredPairing pairing)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, $".pairing-{Guid.NewGuid():N}");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(output, pairing);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Clear() => File.Delete(PathName);
}
