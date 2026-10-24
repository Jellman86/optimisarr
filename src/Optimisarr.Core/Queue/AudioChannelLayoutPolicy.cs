namespace Optimisarr.Core.Queue;

/// <summary>Why a surround channel layout was or was not mapped for an Opus re-encode.</summary>
public enum AudioChannelLayoutOutcome
{
    /// <summary>The audio is not re-encoded to Opus, so its channel layout is left as it is.</summary>
    NotOpus,

    /// <summary>The library turned the automatic conversion off.</summary>
    Disabled,

    /// <summary>The source's channel layout was not reported, so nothing can be decided about it.</summary>
    UnknownLayout,

    /// <summary>The source already uses a layout Opus encodes as it is.</summary>
    AlreadyStandard,

    /// <summary>The audio is downmixed to stereo, which has no surround layout to map.</summary>
    DownmixedToStereo,

    /// <summary>An explicitly set audio filter owns the filter chain and is never overridden.</summary>
    ConflictingFilter,

    /// <summary>The side-surround layout was mapped onto the standard one.</summary>
    Normalised
}

/// <summary>
/// What an Opus re-encode does about the source's surround channel layout. Immutable, and
/// decided from the probed source alone — no database, filesystem, or FFmpeg involvement.
/// </summary>
public sealed record AudioChannelLayoutPlan(
    AudioChannelLayoutOutcome Outcome,
    string? Filter,
    string? SourceLayout,
    string? TargetLayout,
    string? Reason)
{
    /// <summary>True when the layout map belongs in the command.</summary>
    public bool Applies => Outcome == AudioChannelLayoutOutcome.Normalised;

    /// <summary>True when something stood in the way of the automatic conversion.</summary>
    public bool Blocked => Outcome is AudioChannelLayoutOutcome.DownmixedToStereo
        or AudioChannelLayoutOutcome.ConflictingFilter;
}

/// <summary>
/// The audio filter a command applies and what became of the surround layout.
/// </summary>
public sealed record AudioFilterResolution(string? Chain, AudioChannelLayoutPlan Layout)
{
    /// <summary>True when the command carries an audio filter.</summary>
    public bool Applies => Chain is not null;
}

/// <summary>
/// Decides whether an Opus re-encode has to map the source's surround layout.
///
/// Opus writes surround in the standard 5.1 order. A source that declares the side variant
/// (<c>5.1(side)</c>) is mapped onto the standard order so the encoded track's channel order
/// matches the layout its container advertises, instead of leaving the surround channels in the
/// positions the source used.
///
/// Pure and unit tested: it decides from the probed layout and the encoder name alone.
/// </summary>
public static class AudioChannelLayoutPolicy
{
    /// <summary>The surround layout a source declares when its side speakers carry the surrounds.</summary>
    public const string SideSurroundLayout = "5.1(side)";

    /// <summary>The surround layout Opus encodes in.</summary>
    public const string StandardSurroundLayout = "5.1";

    /// <summary>The channel count of the surround layout Opus encodes in.</summary>
    public const int StandardSurroundChannelCount = 6;

    /// <summary>The filter that maps the source's channels onto the given layout.</summary>
    public static string ChannelMapFilter(string targetLayout) => $"channelmap=channel_layout={targetLayout}";

    /// <summary>
    /// Whether the encoder is Opus. Opus fixes the surround channel order, so only its encodes
    /// need the layout mapped.
    /// </summary>
    public static bool IsOpusEncoder(string? encoder)
    {
        var name = encoder?.Trim();
        return name is not null
            && (name.Equals("libopus", StringComparison.OrdinalIgnoreCase)
                || name.Equals("opus", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides what an Opus re-encode does with the source's channel layout.
    /// </summary>
    /// <param name="audioEncoder">The ffmpeg audio encoder the encode uses; null when the audio is copied.</param>
    /// <param name="sourceChannelLayout">The layout the source's audio tracks declare, or null when unknown.</param>
    /// <param name="downmixToStereo">Whether the encode reduces the audio to two channels.</param>
    /// <param name="existingAudioFilter">The audio filter chain already committed to the command, if any.</param>
    /// <param name="enabled">Whether the library asked for the automatic conversion.</param>
    public static AudioChannelLayoutPlan Plan(
        string? audioEncoder,
        string? sourceChannelLayout,
        bool downmixToStereo = false,
        string? existingAudioFilter = null,
        bool enabled = true)
    {
        if (!enabled)
        {
            return new(AudioChannelLayoutOutcome.Disabled, null, sourceChannelLayout, null,
                "The library turned the automatic surround-layout conversion off.");
        }

        if (!IsOpusEncoder(audioEncoder))
        {
            return new(AudioChannelLayoutOutcome.NotOpus, null, sourceChannelLayout, null,
                "The audio is not re-encoded to Opus, so its channel layout is left alone.");
        }

        if (downmixToStereo)
        {
            return new(AudioChannelLayoutOutcome.DownmixedToStereo, null, sourceChannelLayout, null,
                "The audio is downmixed to stereo, so it has no surround layout to map.");
        }

        if (!string.IsNullOrWhiteSpace(existingAudioFilter))
        {
            return new(AudioChannelLayoutOutcome.ConflictingFilter, null, sourceChannelLayout, null,
                "An audio filter is already set for this encode, and an explicit filter is never overridden.");
        }

        var layout = sourceChannelLayout?.Trim();
        if (string.IsNullOrEmpty(layout))
        {
            return new(AudioChannelLayoutOutcome.UnknownLayout, null, null, null,
                "The source's channel layout was not reported, so nothing can be decided about it.");
        }

        if (!layout.Equals(SideSurroundLayout, StringComparison.OrdinalIgnoreCase))
        {
            return new(AudioChannelLayoutOutcome.AlreadyStandard, null, layout, null,
                "The source already uses a channel layout Opus encodes as it is.");
        }

        return new(AudioChannelLayoutOutcome.Normalised, ChannelMapFilter(StandardSurroundLayout),
            layout, StandardSurroundLayout,
            "Opus encodes surround in the standard 5.1 order, so the side variant is mapped onto it.");
    }
}
