namespace Optimisarr.Core.Workers;

/// <summary>Display facts from the server's source probe; never a server filesystem path.</summary>
public sealed record WorkerMediaInfo(string? VideoCodec, int? Width, int? Height,
    double? DurationSeconds, string? Container, string? AudioCodecs, string? PixelFormat);
