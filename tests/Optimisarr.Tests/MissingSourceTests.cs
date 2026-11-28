using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

/// <summary>
/// A queued file that has gone is either a media manager's ordinary upgrade or a library that
/// cannot be reached. Only the first may end quietly (#610).
/// </summary>
public sealed class MissingSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-missing-source-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(LibraryRootState.Listed)]
    [InlineData(LibraryRootState.Empty)]
    [InlineData(LibraryRootState.Missing)]
    [InlineData(LibraryRootState.Unreadable)]
    public void A_source_that_is_still_there_is_present_whatever_its_library_looks_like(LibraryRootState root) =>
        Assert.Equal(MissingSourceVerdict.Present, MissingSource.Classify(sourceExists: true, root));

    [Fact]
    public void A_missing_file_in_a_library_that_still_lists_its_contents_was_replaced_by_a_media_manager() =>
        Assert.Equal(MissingSourceVerdict.Replaced, MissingSource.Classify(sourceExists: false, LibraryRootState.Listed));

    [Theory]
    [InlineData(LibraryRootState.Missing)]
    [InlineData(LibraryRootState.Unreadable)]
    [InlineData(LibraryRootState.Empty)]
    public void A_missing_file_in_a_library_that_cannot_be_seen_is_an_outage(LibraryRootState root) =>
        Assert.Equal(MissingSourceVerdict.LibraryUnavailable, MissingSource.Classify(sourceExists: false, root));

    [Fact]
    public void A_library_folder_that_does_not_exist_is_missing() =>
        Assert.Equal(LibraryRootState.Missing, MissingSource.InspectLibraryRoot(_root));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_library_without_a_folder_is_missing(string? root) =>
        Assert.Equal(LibraryRootState.Missing, MissingSource.InspectLibraryRoot(root));

    [Fact]
    public void An_empty_library_folder_is_reported_as_empty_because_a_dropped_share_leaves_one()
    {
        Directory.CreateDirectory(_root);

        Assert.Equal(LibraryRootState.Empty, MissingSource.InspectLibraryRoot(_root));
    }

    [Fact]
    public void A_library_folder_with_anything_in_it_is_listed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Show", "Season 02"));

        Assert.Equal(LibraryRootState.Listed, MissingSource.InspectLibraryRoot(_root));
    }

    [Fact]
    public void The_outage_message_names_the_folder_so_the_operator_knows_what_to_remount()
    {
        var message = MissingSource.LibraryUnavailableMessage("/data/media/tv");

        Assert.Contains("/data/media/tv", message);
        Assert.Contains("mounted", message);
    }
}
