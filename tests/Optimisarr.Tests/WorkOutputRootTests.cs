using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class WorkOutputRootTests
{
    private static readonly RuleSettings Hevc = RuleProfileDefaults.For(RuleProfile.ConservativeHevc);

    [Fact]
    public void Different_media_files_get_different_work_roots()
    {
        var first = WorkOutputRoot.ForMediaFile("/work", 1);
        var second = WorkOutputRoot.ForMediaFile("/work", 2);

        Assert.Equal("/work/1", first);
        Assert.Equal("/work/2", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Tolerates_a_trailing_slash_on_the_work_root()
    {
        Assert.Equal("/work/7", WorkOutputRoot.ForMediaFile("/work/", 7));
    }

    [Fact]
    public void Preview_outputs_live_in_a_separate_subtree_from_replace_bound_output()
    {
        var preview = WorkOutputRoot.ForPreview("/work", 42);

        Assert.Equal("/work/preview/42", preview);
        // A preview for a job can never collide with a media file's real work output.
        Assert.NotEqual(WorkOutputRoot.ForMediaFile("/work", 42), preview);
    }

    [Fact]
    public void Two_sources_sharing_a_stem_resolve_to_distinct_work_outputs()
    {
        // photo.bmp and photo.tif both target WebP; without per-file namespacing they would
        // both resolve to .../photo.webp and the second job would clobber the first's output.
        var bmp = TranscodeSpecResolver.Resolve(
            Hevc, "/data/photos/photo.bmp", "photo.bmp",
            WorkOutputRoot.ForMediaFile("/work", 1),
            sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Image);
        var tif = TranscodeSpecResolver.Resolve(
            Hevc, "/data/photos/photo.tif", "photo.tif",
            WorkOutputRoot.ForMediaFile("/work", 2),
            sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Image);

        Assert.Equal("/work/1/photo.jpg", bmp.OutputPath);
        Assert.Equal("/work/2/photo.jpg", tif.OutputPath);
        Assert.NotEqual(bmp.OutputPath, tif.OutputPath);
    }
}
