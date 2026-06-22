using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class LibraryAccessEvaluatorTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)] // readable but not writable
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, false)]
    public void IsOk_requires_exists_readable_and_writable(bool exists, bool readable, bool writable, bool expected)
    {
        Assert.Equal(expected, LibraryAccessEvaluator.IsOk(exists, readable, writable));
    }

    [Fact]
    public void Describe_reports_a_missing_path_first()
    {
        Assert.Contains("does not exist", LibraryAccessEvaluator.Describe(exists: false, readable: false, writable: false));
    }

    [Fact]
    public void Describe_reports_unreadable_when_present_but_not_readable()
    {
        Assert.Contains("can't read", LibraryAccessEvaluator.Describe(exists: true, readable: false, writable: false));
    }

    [Fact]
    public void Describe_calls_out_the_replacement_impact_when_not_writable()
    {
        var message = LibraryAccessEvaluator.Describe(exists: true, readable: true, writable: false);

        Assert.Contains("not writable", message);
        Assert.Contains("replacing originals will fail", message);
    }

    [Fact]
    public void Describe_confirms_success_when_fully_accessible()
    {
        Assert.Contains("read and write", LibraryAccessEvaluator.Describe(exists: true, readable: true, writable: true));
    }
}
