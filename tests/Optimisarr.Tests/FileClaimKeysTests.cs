using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class FileClaimKeysTests
{
    [Fact]
    public void Key_namespaces_do_not_overlap_and_hashes_do_not_expose_paths()
    {
        const string path = "/media/private-title.mkv";
        Assert.NotEqual(FileClaimKeys.Path(path, false), FileClaimKeys.Identity(path));
        Assert.DoesNotContain(path, FileClaimKeys.Path(path, false));
        Assert.Equal("media:42", FileClaimKeys.Media(42));
    }

    [Fact]
    public void Case_folding_is_explicit_and_does_not_depend_on_the_current_culture()
    {
        Assert.Equal(FileClaimKeys.Path("/Media/I.mkv", true), FileClaimKeys.Path("/media/i.mkv", true));
        Assert.NotEqual(FileClaimKeys.Path("/Media/I.mkv", false), FileClaimKeys.Path("/media/i.mkv", false));
    }
}
