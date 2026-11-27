using System.Text.Json;

namespace Optimisarr.Core.Library;

/// <summary>
/// The Dolby Vision decoder configuration record a container carries for a video stream (MP4's
/// dvcC/dvvC box, Matroska's block addition mapping), as ffprobe reports it in the stream's side
/// data. A player uses it to decide whether to render the stream as Dolby Vision at all, so a copy
/// that loses or alters it plays as HDR10, SDR or, for Profile 5, with the wrong colours.
/// </summary>
public sealed record DolbyVisionConfiguration(
    int VersionMajor,
    int VersionMinor,
    int Profile,
    int Level,
    bool RpuPresent,
    bool EnhancementLayerPresent,
    bool BaseLayerPresent,
    int BaseLayerCompatibilityId)
{
    /// <summary>
    /// Reads the record from one ffprobe side-data entry. Null when the entry is not a DOVI
    /// configuration record or lacks any field, since a partial record cannot be compared.
    /// </summary>
    public static DolbyVisionConfiguration? FromSideData(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("side_data_type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString()?.Contains("DOVI configuration", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        return Integer(entry, "dv_version_major") is { } major
            && Integer(entry, "dv_version_minor") is { } minor
            && Integer(entry, "dv_profile") is { } profile
            && Integer(entry, "dv_level") is { } level
            && Flag(entry, "rpu_present_flag") is { } rpu
            && Flag(entry, "el_present_flag") is { } enhancementLayer
            && Flag(entry, "bl_present_flag") is { } baseLayer
            && Integer(entry, "dv_bl_signal_compatibility_id") is { } compatibility
            ? new DolbyVisionConfiguration(major, minor, profile, level, rpu, enhancementLayer, baseLayer, compatibility)
            : null;
    }

    /// <summary>The profile as people name it: 5, 7.6, 8.1, 8.4.</summary>
    public string ProfileName => BaseLayerCompatibilityId == 0 ? $"{Profile}" : $"{Profile}.{BaseLayerCompatibilityId}";

    public string Describe()
    {
        var layers = new List<string>(3);
        if (BaseLayerPresent) layers.Add("base layer");
        if (EnhancementLayerPresent) layers.Add("enhancement layer");
        if (RpuPresent) layers.Add("RPU");
        return $"Profile {ProfileName}, level {Level}, version {VersionMajor}.{VersionMinor}, "
            + (layers.Count == 0 ? "no layers" : string.Join(" + ", layers));
    }

    private static int? Integer(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? Flag(JsonElement entry, string property) => Integer(entry, property) switch
    {
        0 => false,
        1 => true,
        _ => null
    };
}
