namespace Optimisarr.Core.Settings;

/// <summary>The animated mark the web UI, favicons and paired sidecars show for this server.</summary>
public enum BrandStyle
{
    Precession,
    Stellar,
}

public static class BrandStyles
{
    /// <summary>Only the two names the UI knows; numbers and anything else are refused.</summary>
    public static bool TryParse(string? value, out BrandStyle style)
    {
        style = BrandStyle.Precession;
        if (string.Equals(value, "stellar", StringComparison.OrdinalIgnoreCase)) style = BrandStyle.Stellar;
        else if (!string.Equals(value, "precession", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string WireName(BrandStyle style) => style == BrandStyle.Stellar ? "stellar" : "precession";
}
