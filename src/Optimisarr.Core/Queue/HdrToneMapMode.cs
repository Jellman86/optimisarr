namespace Optimisarr.Core.Queue;

/// <summary>How an existing HDR-to-SDR library rule executes its tone-map transform.</summary>
public enum HdrToneMapMode
{
    /// <summary>Use the established CPU colour pipeline on every encoder family.</summary>
    Software = 0,

    /// <summary>Use a supported GPU filter with a transparent software retry on failure.</summary>
    Hardware = 1
}
