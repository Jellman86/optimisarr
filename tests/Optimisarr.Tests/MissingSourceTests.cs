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

    [Fact]
    public void A_source_that_is_still_there_is_present_even_without_a_library()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "film.mkv");
        File.WriteAllText(source, "content");

        Assert.Equal((MissingSourceVerdict.Present, (string?)null), MissingSource.Inspect(source, null));
    }

    [PosixFact]
    public void An_unreadable_subfolder_is_reported_even_if_the_library_still_lists()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_root, "unreadable");
        Directory.CreateDirectory(folder);
        var previousMode = File.GetUnixFileMode(folder);
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.None);

            Assert.Equal((MissingSourceVerdict.LibraryUnavailable, folder),
                MissingSource.Inspect(Path.Combine(folder, "film.mkv"), _root));
        }
        finally
        {
            File.SetUnixFileMode(folder, previousMode);
        }
    }

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
