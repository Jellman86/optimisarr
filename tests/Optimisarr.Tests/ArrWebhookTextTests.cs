using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrWebhookTextTests
{
    [Fact]
    public void Control_and_formatting_characters_from_the_caller_are_shown_as_question_marks()
    {
        Assert.Equal("Grab?Forged log line?", ArrWebhookText.Shown("Grab\nForged log line\u202E", 100));
        Assert.Equal("a?b?c", ArrWebhookText.Shown("a\rb\tc", 100));
    }

    [Fact]
    public void Caller_text_longer_than_the_limit_is_cut_and_marked()
    {
        var shown = ArrWebhookText.Shown(new string('x', 5000), 64);

        Assert.Equal(64, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void Cutting_never_splits_a_character_outside_the_basic_plane()
    {
        var shown = ArrWebhookText.Shown(new string('x', 62) + "😀😀", 64);

        Assert.Equal(new string('x', 62) + "…", shown);
    }

    [Fact]
    public void Plain_text_within_the_limit_is_unchanged()
    {
        Assert.Equal("/tv/Show/S01E01 – Pilot.mkv", ArrWebhookText.Shown("/tv/Show/S01E01 – Pilot.mkv", 512));
    }
}
