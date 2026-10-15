using System.Text;

namespace Optimisarr.Core.Workers;

/// <summary>An opaque physical capacity identity, never a path or a display name.</summary>
public sealed record PhysicalVolumeId
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public string Value { get; }
    internal bool IsValid => Valid(Value);

    public PhysicalVolumeId(string value)
    {
        if (!Valid(value)) throw new ArgumentException("A physical volume requires a bounded, nonempty identity.", nameof(value));
        Value = value;
    }

    private static bool Valid(string? value)
    {
        try { return !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && StrictUtf8.GetByteCount(value) <= 512; }
        catch (EncoderFallbackException) { return false; }
    }
}
