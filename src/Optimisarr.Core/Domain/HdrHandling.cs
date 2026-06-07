namespace Optimisarr.Core.Domain;

/// <summary>How a library treats HDR / Dolby Vision content.</summary>
public enum HdrHandling
{
    /// <summary>Re-encode while preserving the HDR signal (no tone-mapping).</summary>
    Preserve = 0,

    /// <summary>Leave HDR content untouched — never a candidate. The safe default.</summary>
    Exclude = 1,

    /// <summary>Tone-map HDR down to SDR during the transcode.</summary>
    TonemapToSdr = 2
}
