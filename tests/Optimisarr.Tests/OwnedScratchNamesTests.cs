using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class OwnedScratchNamesTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("adaptive-quality")]
    [InlineData("verification-reference")]
    public void A_workspace_name_round_trips_to_its_jobs_nonce(string purpose) =>
        Assert.Equal(Nonce, OwnedScratchNames.NonceFor(OwnedScratchNames.DirectoryName(purpose, 16, Nonce), 16));

    [Theory]
    [InlineData(".adaptive-quality-160-0123456789abcdef0123456789abcdef")]
    [InlineData(".adaptive-quality-16-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData(".adaptive-quality-16-0123456789abcdef")]
    [InlineData(".adaptive-quality-16-0123456789abcdef0123456789abcdef-x")]
    [InlineData("adaptive-quality-16-0123456789abcdef0123456789abcdef")]
    [InlineData(".other-16-0123456789abcdef0123456789abcdef")]
    public void Any_other_folder_is_not_this_jobs_workspace(string name) =>
        Assert.Null(OwnedScratchNames.NonceFor(name, 16));
}
