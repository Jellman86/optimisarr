using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Workers;

/// <summary>
/// The outcome of comparing a sidecar's supported protocol range against this control plane's.
/// <see cref="AgreedVersion"/> is zero whenever <see cref="Compatible"/> is false, so a caller
/// cannot mistake a refusal for a usable version.
/// </summary>
public sealed record ProtocolNegotiation(bool Compatible, int AgreedVersion, string? Reason)
{
    public static ProtocolNegotiation Agreed(int version) => new(true, version, null);

    public static ProtocolNegotiation Refused(string reason) => new(false, 0, reason);
}

/// <summary>
/// The versioned wire contract between the control plane and a remote transcoding sidecar.
/// The main app owns the contract: a worker built against a newer protocol falls back to what
/// this build speaks, never the other way round.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>The newest contract version this build speaks.</summary>
    // Protocol 14 fixes soundtrack duration qualification and near-start decoder pre-roll.
    public const int Current = 14;

    // Advertise this only once all native runners and strict verification implement the contract.
    public const int PerceptualQualityVersion = 11;

    /// <summary>Video encodes that give each output soundtrack its own measured bitrate (<c>-b:a:N</c>).</summary>
    public const int TrackBitrateVersion = 12;

    /// <summary>Soundtrack gates that measure a downmixed track against the same downmix of its source.</summary>
    public const int DownmixSoundtrackVersion = 13;

    /// <summary>Exact FLAC statistics durations and picture-timeline trimming through codec pre-roll.</summary>
    public const int SoundtrackTimingVersion = 14;

    public static int MinimumForEncodeCommand(IReadOnlyList<string> arguments, MediaKind kind = MediaKind.Video) =>
        arguments.Any(argument => argument.StartsWith("-b:a:", StringComparison.Ordinal)) ? TrackBitrateVersion :
        kind == MediaKind.Image ? PerceptualQualityVersion : arguments.Contains("-itsoffset") ? 9 : kind == MediaKind.Audio ? 6 : arguments.Any(argument => argument is "-color_range:v:0" or "-bsf:v:0") ? 5
            : arguments.Any(argument => argument.StartsWith("-c:s:", StringComparison.Ordinal)) ? 4 : 1;

    public static int MinimumForAssignment(IReadOnlyList<string> arguments, MediaKind kind, string model, bool measuresVmaf,
        bool measuresSoundtracks = false, bool countsVideoFrames = false, bool perceptualSearch = false, bool measuresDownmixedSoundtracks = false) =>
        Math.Max(Math.Max(MinimumForEncodeCommand(arguments, kind), measuresSoundtracks || measuresDownmixedSoundtracks ? SoundtrackTimingVersion : 1),
            perceptualSearch ? PerceptualQualityVersion : countsVideoFrames ? 9
            : measuresVmaf && model.StartsWith("vmaf_v1.", StringComparison.Ordinal) ? 7 : 1);

    /// <summary>The oldest contract version this build still accepts.</summary>
    public const int MinimumSupported = 1;

    /// <summary>
    /// Stands in for the worker's local copy of the source inside an assignment's argument array.
    /// The server never sends a path: the source route takes only a lease, and the worker decides
    /// where its scratch lives. A worker substitutes its own paths and nothing else.
    /// </summary>
    public const string InputPlaceholder = "{{input}}";

    /// <summary>
    /// Stands in for the worker's candidate output. It appears as a prefix carrying the container
    /// extension the server chose (<c>{{output}}.mkv</c>), because the extension is part of the
    /// encode contract — it decides subtitle codecs and muxer — and must not be the worker's guess.
    /// </summary>
    public const string OutputPlaceholder = "{{output}}";

    /// <summary>
    /// Picks the highest version both sides support, or refuses with a reason. Refusal is the
    /// safe outcome: assuming an unknown sidecar is compatible is exactly the silent-upgrade
    /// failure this negotiation exists to prevent.
    /// </summary>
    public static ProtocolNegotiation Negotiate(int workerMinimum, int workerMaximum)
    {
        if (workerMinimum > workerMaximum)
        {
            return ProtocolNegotiation.Refused(
                $"The worker reported an inverted protocol range ({workerMinimum}–{workerMaximum}).");
        }

        if (workerMaximum < MinimumSupported)
        {
            return ProtocolNegotiation.Refused(
                $"The worker speaks an older protocol than this build supports " +
                $"(worker up to {workerMaximum}, minimum supported {MinimumSupported}). Upgrade the worker.");
        }

        if (workerMinimum > Current)
        {
            return ProtocolNegotiation.Refused(
                $"The worker requires a newer protocol than this build speaks " +
                $"(worker from {workerMinimum}, current {Current}). Upgrade Optimisarr.");
        }

        return ProtocolNegotiation.Agreed(Math.Min(workerMaximum, Current));
    }
}
