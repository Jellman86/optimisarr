using System.Security.Cryptography;
using System.Text;

namespace Optimisarr.Core.Deduplication;

public static class FileClaimKeys
{
    public static string Media(int id) => $"media:{id}";
    public static string Path(string canonical, bool ignoreCase) => "path:" + Digest(ignoreCase ? canonical.ToUpperInvariant() : canonical);
    public static string Identity(string identity) => "identity:" + Digest(identity);
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
