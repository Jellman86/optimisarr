using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrImportExclusionEvaluatorTests
{
    [Fact]
    public void A_file_inside_an_active_import_folder_is_excluded_with_a_reason()
    {
        var imports = new[] { new ArrActiveImport("Sonarr", "/data/tv/Show A") };

        var reason = ArrImportExclusionEvaluator.ExclusionReason("/data/tv/Show A/Season 01/ep.mkv", imports);

        Assert.NotNull(reason);
        Assert.Contains("Sonarr", reason);
        Assert.Contains("/data/tv/Show A", reason);
    }

    [Fact]
    public void A_file_outside_every_active_folder_is_not_excluded()
    {
        var imports = new[] { new ArrActiveImport("Sonarr", "/data/tv/Show A") };

        Assert.Null(ArrImportExclusionEvaluator.ExclusionReason("/data/tv/Show B/ep.mkv", imports));
    }

    [Fact]
    public void No_active_imports_excludes_nothing()
    {
        Assert.Null(ArrImportExclusionEvaluator.ExclusionReason("/data/tv/Show A/ep.mkv", []));
    }

    [Theory]
    [InlineData("/data/tv/Show", "/data/tv/Show", true)]
    [InlineData("/data/tv/Show/ep.mkv", "/data/tv/Show", true)]
    [InlineData("/data/tv/Show/", "/data/tv/Show", true)]
    [InlineData("/data/tv/Show 2/ep.mkv", "/data/tv/Show", false)]
    [InlineData("/data/tv/Showcase/ep.mkv", "/data/tv/Show", false)]
    [InlineData("/data/tv/ep.mkv", "/data/tv/Show", false)]
    public void IsWithin_matches_on_path_segments_not_string_prefixes(string path, string folder, bool expected)
    {
        Assert.Equal(expected, ArrImportExclusionEvaluator.IsWithin(path, folder));
    }
}
