using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Activity;

/// <summary>An exact media-server image identity, restricted to its own image endpoints.</summary>
public sealed partial record PlaybackArtwork
{
    private PlaybackArtwork(string path) => Path = path;
    public string Path { get; }

    public static PlaybackArtwork? Plex(string? path) =>
        path is { Length: <= 256 } && PlexPath().IsMatch(path) ? new(path) : null;

    public static PlaybackArtwork? MediaBrowser(string? itemId, string? tag)
    {
        if (tag is null || !ImageTag().IsMatch(tag)) return null;
        var id = Guid.TryParse(itemId, out var guid) && guid != Guid.Empty ? guid.ToString("N")
            : itemId is { Length: <= 20 } && NumericId().IsMatch(itemId)
                && ulong.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                    ? itemId : null;
        return id is null ? null : new($"/Items/{id}/Images/Primary?maxWidth=240&quality=85&tag={tag}");
    }

    public string ProxyKey(int watcherId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{watcherId}:{Path}")));

    [GeneratedRegex(@"\A/library/metadata/[0-9]+/thumb(?:/[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlexPath();
    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericId();
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();
}
