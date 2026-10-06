using System;
using System.Collections.Generic;
using System.Globalization;

namespace Optimisarr.Sidecar.Tray;

/// <summary>
/// The colours of what a state means.
/// </summary>
/// <remarks>
/// Everything else in the tray is Windows' own Fluent theme, so it looks like the PC it runs on
/// and follows its light, dark, accent and contrast settings. The exception is a state: the web
/// interface, this tray and the Mac menu say "working", "no server" and "revoked" in the same
/// colours, chosen to stay apart under colour-blindness — success is blue rather than green, a
/// hold-up gold, a fault raspberry. Each value is written exactly as <c>web/src/app.css</c> writes
/// it, with the token named in the comment; <c>scripts/tests/test_sidecar_theme.py</c> fails if
/// this file, the Mac menu's <c>Instrument.swift</c> and the stylesheet drift apart. The key is
/// the XAML resource name.
/// </remarks>
internal static class SidecarTheme
{
    internal sealed record Token(string Key, string Light, string Dark);

    internal static readonly IReadOnlyList<Token> Tokens =
    [
        new("Ok", Light: "#0062cc", Dark: "#4ea1ff"),                                      // --ok
        new("OkStrong", Light: "#004593", Dark: "#bcdaff"),                                // --ok-strong
        new("OkSoft", Light: "rgba(0, 98, 204, 0.1)", Dark: "rgba(78, 161, 255, 0.15)"),   // --ok-soft
        new("Warn", Light: "#946b00", Dark: "#f5cc4f"),                                    // --warn
        new("WarnStrong", Light: "#5c4200", Dark: "#fde7a2"),                              // --warn-strong
        new("WarnSoft", Light: "rgba(214, 160, 0, 0.17)", Dark: "rgba(245, 204, 79, 0.14)"), // --warn-soft
        new("Bad", Light: "#d0216e", Dark: "#ff6b9a"),                                     // --bad
        new("BadStrong", Light: "#8e0f48", Dark: "#ffc6d8"),                               // --bad-strong
        new("BadSoft", Light: "rgba(208, 33, 110, 0.1)", Dark: "rgba(255, 107, 154, 0.14)"), // --bad-soft
        new("Info", Light: "#4f4f57", Dark: "#b4b4bb"),                                    // --info
        new("InfoStrong", Light: "#2c2c31", Dark: "#e6e6eb"),                              // --info-strong
        new("InfoSoft", Light: "rgba(118, 118, 128, 0.13)", Dark: "rgba(142, 142, 150, 0.18)"), // --info-soft
    ];

    /// <summary>A colour written as the stylesheet writes it: <c>#rrggbb</c> or <c>rgba(r, g, b, a)</c>.</summary>
    internal static (byte A, byte R, byte G, byte B) Parse(string css)
    {
        var text = css.Trim().ToLowerInvariant();
        if (text.Length == 7 && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return (255, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        if (text.StartsWith("rgba(", StringComparison.Ordinal) && text.EndsWith(')'))
        {
            var parts = text[5..^1].Split(',');
            if (parts.Length == 4
                && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                && alpha is >= 0 and <= 1)
                return ((byte)Math.Round(alpha * 255), r, g, b);
        }
        throw new FormatException("Unsupported colour token: " + css);
    }
}
