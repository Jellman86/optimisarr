using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class LibraryPathSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-path-safety-").FullName;

    [Fact]
    public void Only_a_regular_file_strictly_below_the_library_is_allowed()
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        var file = Path.Combine(library, "movie.mkv");
        File.WriteAllText(file, "original");
        var sibling = Directory.CreateDirectory(library + "-sibling").FullName;
        var outside = Path.Combine(sibling, "movie.mkv");
        File.WriteAllText(outside, "outside");

        Assert.True(LibraryPathSafety.IsSafeFile(file, library + Path.DirectorySeparatorChar));
        Assert.False(LibraryPathSafety.IsSafeFile(outside, library));
        Assert.False(LibraryPathSafety.IsSafeFile(library, library));
        Assert.False(LibraryPathSafety.IsSafeFile(Path.Combine(library, "..", "library-sibling", "movie.mkv"), library));
        Assert.False(LibraryPathSafety.IsSafeFile("relative.mkv", library));
        Assert.False(LibraryPathSafety.IsSafeFile(file, "relative"));
        Assert.False(LibraryPathSafety.IsSafeFile(Path.Combine(library, "missing.mkv"), library));
        Assert.True(LibraryPathSafety.IsSafeFile(Path.Combine(library, "missing.mkv"), library, allowMissingLeaf: true));
        Assert.False(LibraryPathSafety.IsSafeFile(Path.Combine(library, "missing", "movie.mkv"), library, allowMissingLeaf: true));
    }

    [PosixFact]
    public void Links_at_the_root_or_below_it_are_refused_but_operator_owned_ancestors_are_allowed()
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        var file = Path.Combine(library, "movie.mkv");
        File.WriteAllText(file, "original");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _root);
        var trustedRoot = Path.Combine(alias, "library");
        Assert.True(LibraryPathSafety.IsSafeFile(Path.Combine(trustedRoot, "movie.mkv"), trustedRoot));
        Assert.False(LibraryPathSafety.IsSafeFile(Path.Combine(alias, "library", "movie.mkv"), alias));
        var leaf = Path.Combine(library, "link.mkv");
        File.CreateSymbolicLink(leaf, file);
        Assert.False(LibraryPathSafety.IsSafeFile(leaf, library));
        File.Delete(file);
        Assert.False(LibraryPathSafety.IsSafeFile(leaf, library, allowMissingLeaf: true));
        Assert.False(LibraryPathSafety.IsSafeFile(Path.Combine(trustedRoot, "movie.mkv"), allowMissingLeaf: true));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
