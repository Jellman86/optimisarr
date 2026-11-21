namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// How a source's YUV picture maps to RGB and back. The engine works on RGB, so the decoder must
/// convert with the source's own matrix and range, and the encoder must convert back the same way
/// and restore the source's colour tags; otherwise a BT.709 picture comes back as untagged BT.601.
/// <see cref="Matrix"/> and <see cref="Range"/> are the conversion (FFmpeg scale names); the tags
/// are written only when the source carried them.
/// </summary>
public sealed record SuperResolutionColour(
    string Matrix, string Range, string? Primaries, string? Transfer, string? Space, bool RangeTagged = false)
{
    public bool Tagged => Primaries is not null || Transfer is not null || Space is not null;
}
