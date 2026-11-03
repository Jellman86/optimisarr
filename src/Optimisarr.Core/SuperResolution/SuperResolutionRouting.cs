using Optimisarr.Core.Domain;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The single rule for whether a job is super-resolution work. The experimental feature switch is
/// the gate: while it is off, a library that opted in behaves exactly like any other library.
/// Previews and calibration samples exist to show or compare a normal encode, so they never take
/// the engine path, and audio or still-image jobs have no frames to upscale.
/// </summary>
public static class SuperResolutionRouting
{
    public static bool Applies(bool featureEnabled, bool libraryOptedIn, MediaKind kind, bool replaceBound) =>
        featureEnabled && libraryOptedIn && kind == MediaKind.Video && replaceBound;
}
