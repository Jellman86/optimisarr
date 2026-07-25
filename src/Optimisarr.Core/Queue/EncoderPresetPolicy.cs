namespace Optimisarr.Core.Queue;

/// <summary>
/// The portable operator choice and exact encoder-specific preset passed to FFmpeg.
/// A null FFmpeg preset means that encoder must use its own driver default.
/// </summary>
public sealed record EncoderPresetResolution(
    bool Succeeded,
    string? Effort,
    string? FfmpegPreset,
    string? Error);

/// <summary>
/// Maps Optimisarr's portable encoder-effort choices onto the preset vocabulary of the exact
/// encoder selected at dispatch. This keeps Auto mode safe: the library stores intent, while the
/// resolved CPU, NVENC, QSV, SVT-AV1, or VAAPI encoder receives only an option it supports.
/// </summary>
public static class EncoderPresetPolicy
{
    // "quick" is intentionally distinct from the former exact x26x "fast" value. That lets the
    // API preserve an existing raw preset without confusing it with the new portable Fast choice.
    public const string Quick = "quick";
    public const string Balanced = "balanced";
    public const string Efficient = "efficient";

    private static readonly string[] X26xEffortPresets =
        ["ultrafast", "veryfast", "faster", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] SvtAv1EffortPresets = ["13", "11", "9", "8", "6", "4", "2"];
    private static readonly string[] QsvEffortPresets =
        ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    public static IReadOnlyList<string> Selections { get; } =
        Array.AsReadOnly([Quick, Balanced, Efficient]);

    public static IReadOnlyList<string> LegacySelections { get; } = Array.AsReadOnly(
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow",
        "p1", "p2", "p3", "p4", "p5", "p6", "p7",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13"
    ]);

    public static bool TryNormaliseSelection(string? selection, out string? normalised)
    {
        var value = selection?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value is "-2" or "-1")
        {
            normalised = null;
            return true;
        }

        if (value is Quick or Balanced or Efficient || LegacySelections.Contains(value, StringComparer.Ordinal))
        {
            normalised = value;
            return true;
        }

        normalised = null;
        return false;
    }

    public static EncoderPresetResolution Resolve(string? encoder, string? selection)
    {
        if (!TryNormaliseSelection(selection, out var effort))
        {
            return Failure(
                $"Invalid encoder effort '{selection?.Trim()}'. Choose encoder default, fast, balanced, or efficient.");
        }

        if (effort is null)
        {
            return Success(null, null);
        }

        var name = encoder?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
        {
            return Failure(
                $"Encoder effort '{effort}' cannot be resolved without a selected encoder. Choose encoder default.");
        }

        if (effort is Quick or Balanced or Efficient)
        {
            return ResolvePortable(name, effort);
        }

        return ResolveLegacy(name, effort);
    }

    private static EncoderPresetResolution ResolvePortable(string encoder, string effort)
    {
        if (encoder is "libx264" or "libx265")
        {
            return Success(effort, effort switch
            {
                Quick => "fast",
                Balanced => "medium",
                _ => "slow"
            });
        }

        if (encoder == "libsvtav1")
        {
            return Success(effort, effort switch
            {
                Quick => "10",
                Balanced => "8",
                _ => "6"
            });
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return Success(effort, effort switch
            {
                Quick => "p2",
                Balanced => "p4",
                _ => "p7"
            });
        }

        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return Success(effort, effort switch
            {
                Quick => "fast",
                Balanced => "medium",
                _ => "slow"
            });
        }

        if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return Success(effort, null);
        }

        return Failure(
            $"Encoder effort '{effort}' cannot be resolved for encoder '{encoder}'. Choose encoder default.");
    }

    private static EncoderPresetResolution ResolveLegacy(string encoder, string selection)
    {
        if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            return Success(selection, null);
        }

        if (encoder is "libx264" or "libx265" && IsX26xPreset(selection))
        {
            return Success(selection, selection);
        }

        if (encoder == "libsvtav1" && int.TryParse(selection, out var svtPreset) && svtPreset is >= 0 and <= 13)
        {
            return Success(selection, selection);
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal) && IsNvencPreset(selection))
        {
            return Success(selection, selection);
        }

        if (encoder.EndsWith("_qsv", StringComparison.Ordinal) && IsQsvPreset(selection))
        {
            return Success(selection, selection);
        }

        var level = LegacyEffortLevel(selection);
        if (level is null)
        {
            return Failure(
                $"Invalid encoder effort '{selection}'. Choose encoder default, fast, balanced, or efficient.");
        }

        if (encoder is "libx264" or "libx265")
        {
            return Success(selection, X26xEffortPresets[level.Value - 1]);
        }

        if (encoder == "libsvtav1")
        {
            return Success(selection, SvtAv1EffortPresets[level.Value - 1]);
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return Success(selection, $"p{level.Value}");
        }

        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            return Success(selection, QsvEffortPresets[level.Value - 1]);
        }

        return Failure(
            $"Encoder effort '{selection}' cannot be resolved for encoder '{encoder}'. Choose encoder default.");
    }

    private static int? LegacyEffortLevel(string selection)
    {
        if (selection.Length == 2 && selection[0] == 'p' && selection[1] is >= '1' and <= '7')
        {
            return selection[1] - '0';
        }

        if (int.TryParse(selection, out var svtPreset))
        {
            return svtPreset switch
            {
                >= 12 and <= 13 => 1,
                >= 10 and <= 11 => 2,
                9 => 3,
                >= 7 and <= 8 => 4,
                >= 5 and <= 6 => 5,
                >= 3 and <= 4 => 6,
                >= 0 and <= 2 => 7,
                _ => null
            };
        }

        return selection switch
        {
            "ultrafast" or "superfast" => 1,
            "veryfast" => 2,
            "faster" or "fast" => 3,
            "medium" => 4,
            "slow" => 5,
            "slower" => 6,
            "veryslow" => 7,
            _ => null
        };
    }

    private static bool IsX26xPreset(string value) =>
        value is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast"
            or "medium" or "slow" or "slower" or "veryslow";

    private static bool IsNvencPreset(string value) =>
        value.Length == 2 && value[0] == 'p' && value[1] is >= '1' and <= '7';

    private static bool IsQsvPreset(string value) =>
        value is "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow";

    private static EncoderPresetResolution Success(string? effort, string? preset) =>
        new(true, effort, preset, null);

    private static EncoderPresetResolution Failure(string error) =>
        new(false, null, null, error);
}
