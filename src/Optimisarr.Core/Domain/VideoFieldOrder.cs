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
    public static bool IsInterlaced(string? fieldOrder) => fieldOrder is "tt" or "bb" or "tb" or "bt";

    /// <summary>Why this source is left alone, for the inventory and the queue to show.</summary>
    public static string RefusalFor(string fieldOrder) =>
        $"Interlaced video (field order {fieldOrder}): re-encoding would bake in the combing and drop the "
        + "interlace flag, so it is left untouched";
}
