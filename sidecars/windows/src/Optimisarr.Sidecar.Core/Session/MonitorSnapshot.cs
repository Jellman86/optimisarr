namespace Optimisarr.Sidecar.Core.Session;

/// <summary>A local, credential-free view of the worker for its tray companion.</summary>
public sealed record MonitorJob(int JobId, string Title, string Encoder, RemoteStage Stage, double? EncodedSeconds,
    byte[]? PreviewJpeg = null);
public sealed record MonitorSnapshot(
    string Machine, string State, string Detail, bool Paused, string? ServerAddress,
    MachineLoad? Load, long? FreeBytes, IReadOnlyList<MonitorJob> Jobs, string? LastOutcome,
    string Version, bool ShutdownArmed = false, string? ShutdownDetail = null, int? ShutdownSeconds = null,
    bool ShutdownCanCancel = true);

public static class MonitorProtocol
{
    public const string PipeName = "Optimisarr.Sidecar.Monitor.v1";
    public const byte Read = 0;
    public const byte Pause = 1;
    public const byte Resume = 2;
    public const byte ReadPreview = 3;
    public const byte EndPreview = 4;
    public const byte ArmShutdown = 5;
    public const byte CancelShutdown = 6;
    public const int MaximumResponseBytes = 64 * 1024;
    public const int MaximumPreviewBytes = 8 * 1024;
    public const int MaximumPreviewJobs = 4;

    public static string? PublicServerAddress(string? address)
    {
        var uri = ServerUri(address);
        return uri?.GetLeftPart(UriPartial.Path);
    }

    public static Uri? ServerUri(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && Uri.TryCreate(address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address,
            UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
}
