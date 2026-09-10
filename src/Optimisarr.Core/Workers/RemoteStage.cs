namespace Optimisarr.Core.Workers;

/// <summary>
/// Where a remote worker is in a job it holds, as the worker reports it with each renewal. Named
/// rather than numbered because the sidecars are versioned separately from the server.
/// </summary>
public enum RemoteStage
{
    /// <summary>The source is being sent to the worker.</summary>
    FetchingSource = 0,

    /// <summary>ffmpeg is running on the worker.</summary>
    Encoding = 1,

    /// <summary>The candidate is being returned to the server.</summary>
    Delivering = 2,

    /// <summary>libvmaf is scoring the candidate against the source on the worker.</summary>
    Measuring = 3
}
