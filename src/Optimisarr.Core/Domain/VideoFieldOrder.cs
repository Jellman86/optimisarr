namespace Optimisarr.Core.Domain;

/// <summary>
/// What ffprobe's field order says about a video stream. An interlaced source re-encoded by the
/// normal pipeline keeps its woven pictures but is written as progressive, so players stop
/// deinterlacing and it plays combed, while every verification check still passes because source
/// and candidate are compared weave to weave. Only an explicit interlaced value counts: modern
/// codecs commonly report "unknown", which says nothing either way.
/// </summary>
public static class VideoFieldOrder
{
    public static readonly string[] InterlacedValues = ["tt", "bb", "tb", "bt"];

    public static bool IsInterlaced(string? fieldOrder) => fieldOrder is not null && InterlacedValues.Contains(fieldOrder);

    /// <summary>Why this source is left alone, for the inventory and the queue to show.</summary>
    public static string RefusalFor(string fieldOrder) =>
        $"Interlaced video (field order {fieldOrder}): re-encoding would bake in the combing and drop the "
        + "interlace flag, so it is left untouched. A library can choose to deinterlace these";

    /// <summary>
    /// Deinterlaces at the source's frame rate, so every frame still pairs with one frame of the
    /// original. Parity comes from the decoded frames, not the probe: ffprobe's "tb" is commonly a
    /// top-field-first source, so deriving parity from its name would pick the wrong field first.
    /// </summary>
    public const string DeinterlaceFilter = "bwdif=mode=send_frame:parity=auto:deint=all";

    /// <summary>
    /// Whether this source is deinterlaced. Such a job runs on the server: a worker's quality
    /// contract has no way to deinterlace its reference, so it would judge a progressive candidate
    /// against woven fields.
    /// </summary>
    public static bool Deinterlaces(bool libraryOptedIn, string? fieldOrder) => libraryOptedIn && IsInterlaced(fieldOrder);
}
