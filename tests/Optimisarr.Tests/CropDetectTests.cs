using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// Black-bar removal from #95. Two pure pieces: reading what FFmpeg's cropdetect reports, and
/// deciding what to do with several reports that disagree.
///
/// The planner is where the safety lives. The VMAF gate cannot catch a wrong crop — the
/// reference is cropped identically, so a crop that removes picture scores as well as one that
/// removes bars. Every rule here therefore errs toward keeping picture: the union of everything
/// any sampled window kept, a floor on how much may go, and nothing at all when the samples
/// cannot agree.
/// </summary>
public sealed class CropDetectTests
{
    // Verbatim shape of a cropdetect line from a real ffmpeg run on a 2.39:1 film in a 16:9 frame.
    private const string Scope =
        "[Parsed_cropdetect_0 @ 0x7f8] x1:0 x2:1919 y1:138 y2:941 w:1920 h:800 x:0 y:140 pts:1200 t:50.000000 crop=1920:800:0:140";

    // --- Parsing ------------------------------------------------------------------------------

    [Fact]
    public void A_cropdetect_line_is_read_into_a_rectangle()
    {
        var rect = CropDetectParser.ParseLine(Scope);

        Assert.Equal(new CropRect(Width: 1920, Height: 800, X: 0, Y: 140), rect);
    }

    [Fact]
    public void Lines_that_are_not_cropdetect_output_are_ignored()
    {
        Assert.Null(CropDetectParser.ParseLine("frame=  120 fps=0.0 q=-0.0 size=N/A time=00:00:05.00"));
        Assert.Null(CropDetectParser.ParseLine("[Parsed_cropdetect_0 @ 0x7f8] something unexpected"));
        Assert.Null(CropDetectParser.ParseLine(""));
    }

    [Fact]
    public void A_whole_stderr_yields_every_detection_in_order()
    {
        var stderr = string.Join("\n",
            "ffmpeg version n7.1",
            Scope,
            "frame=   60 fps=0.0",
            "[Parsed_cropdetect_0 @ 0x7f8] x1:0 x2:1919 y1:0 y2:1079 w:1920 h:1080 x:0 y:0 pts:2400 t:100.000000 crop=1920:1080:0:0");

        var rects = CropDetectParser.ParseAll(stderr);

        Assert.Equal(2, rects.Count);
        Assert.Equal(800, rects[0].Height);
        Assert.Equal(1080, rects[1].Height);
    }

    // --- Planning -----------------------------------------------------------------------------

    private static readonly PictureSize Source = new(1920, 1080);

    [Fact]
    public void Consistent_bars_across_every_window_are_cropped()
    {
        var samples = new[]
        {
            new CropRect(1920, 800, 0, 140),
            new CropRect(1920, 800, 0, 140),
            new CropRect(1920, 800, 0, 140)
        };

        var plan = CropPlanner.Plan(samples, Source);

        Assert.Equal(new CropRect(1920, 800, 0, 140), plan);
    }

    [Fact]
    public void The_plan_keeps_everything_any_window_kept()
    {
        // A dark scene reports a tighter crop than a bright one. Taking the tight crop would cut
        // picture from every bright scene in the film. The union of retained regions is the only
        // crop that is safe for all of them — and VMAF would never notice the difference.
        var samples = new[]
        {
            new CropRect(1920, 800, 0, 140),   // the real bars
            new CropRect(1920, 640, 0, 220),   // a night scene, over-detected
            new CropRect(1920, 800, 0, 140)
        };

        var plan = CropPlanner.Plan(samples, Source);

        Assert.Equal(new CropRect(1920, 800, 0, 140), plan);
    }

    [Fact]
    public void One_window_showing_no_bars_means_no_crop()
    {
        // Variable-aspect material: a film that opens 2.39:1 and switches to 16:9 for its IMAX
        // sequences. If any sampled window filled the frame, the frame is picture somewhere, and
        // cropping it would destroy those scenes. Nothing is cropped.
        var samples = new[]
        {
            new CropRect(1920, 800, 0, 140),
            new CropRect(1920, 1080, 0, 0),
            new CropRect(1920, 800, 0, 140)
        };

        Assert.Null(CropPlanner.Plan(samples, Source));
    }

    [Fact]
    public void A_trivial_crop_is_not_worth_a_different_picture()
    {
        // Two or four pixels of bar per edge is encoder noise, not letterboxing. Changing the
        // output geometry for that costs a re-verification against a cropped reference and buys
        // nothing measurable, so it is left alone.
        var samples = new[] { new CropRect(1920, 1072, 0, 4), new CropRect(1920, 1072, 0, 4) };

        Assert.Null(CropPlanner.Plan(samples, Source));
    }

    [Fact]
    public void Removing_more_than_the_ceiling_is_refused_as_implausible()
    {
        // No real letterbox removes most of the frame. A plan that would is a detection failure —
        // a fade, a very dark title sequence — and the safe reading is that there are no bars.
        var samples = new[] { new CropRect(1920, 400, 0, 340), new CropRect(1920, 400, 0, 340) };

        Assert.Null(CropPlanner.Plan(samples, Source));
    }

    [Fact]
    public void Odd_crop_edges_are_widened_outward_to_even()
    {
        // 4:2:0 chroma needs even dimensions and offsets. Rounding *outward* keeps a sliver of bar
        // rather than shaving a line of picture, which is the only acceptable direction.
        var samples = new[] { new CropRect(1920, 801, 0, 139), new CropRect(1920, 801, 0, 139) };

        var plan = CropPlanner.Plan(samples, Source);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.Height % 2);
        Assert.Equal(0, plan.Y % 2);
        Assert.True(plan.Y <= 139 && plan.Y + plan.Height >= 139 + 801);
    }

    [Fact]
    public void No_samples_means_no_crop()
    {
        Assert.Null(CropPlanner.Plan(Array.Empty<CropRect>(), Source));
    }

    [Fact]
    public void Pillarboxing_is_cropped_the_same_way_as_letterboxing()
    {
        // 4:3 material in a 16:9 frame: bars on the sides rather than top and bottom.
        var samples = new[] { new CropRect(1440, 1080, 240, 0), new CropRect(1440, 1080, 240, 0) };

        Assert.Equal(new CropRect(1440, 1080, 240, 0), CropPlanner.Plan(samples, Source));
    }

    // --- The filter ---------------------------------------------------------------------------

    [Fact]
    public void The_crop_filter_states_all_four_numbers_explicitly()
    {
        Assert.Equal("crop=1920:800:0:140", CropPlanner.Filter(new CropRect(1920, 800, 0, 140)));
    }
}
