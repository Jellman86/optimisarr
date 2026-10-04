using System.Globalization;
using System.Text;

namespace Optimisarr.Sidecar.Service;

/// <summary>A small availability notice, without log messages, paths or exception text.</summary>
internal sealed class EventLogAvailabilityFile(string path, TimeProvider clock)
{
    internal const int MaximumBytes = 16 * 1024;

    public void Write(EventLogWriteState state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
            File.Move(path, path + ".previous", overwrite: true);
        var detail = state.Available
            ? "Event Log writes recovered."
            : $"Event Log writes unavailable (native code {state.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}). Worker continues; retry in 30 seconds.";
        File.AppendAllText(path, $"{clock.GetUtcNow():O} {detail}\n", Encoding.UTF8);
    }
}
