namespace Optimisarr.Core.Domain;

/// <summary>The kind of content a library holds. Drives which files a scan discovers and which
/// optimisation rules a library exposes.</summary>
public enum MediaType
{
    Film = 0,
    Tv = 1,
    Music = 2,
    Other = 3,
    /// <summary>A still-image library (photos). Discovers image files and exposes the image rules.</summary>
    Photo = 4
}
