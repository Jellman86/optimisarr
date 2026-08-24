namespace Optimisarr.Tests;

/// <summary>
/// Runs the classes that boot the tokened host sequentially against one shared instance.
///
/// Two reasons they cannot run in parallel. <see cref="AdminTokenAuthEndpointTests.TokenedApi"/>
/// sets <c>OPTIMISARR_CONFIG_DIR</c>, which is process-wide, so concurrent fixtures would race to
/// decide which database the host actually opens. And the remote-worker tests toggle a global
/// setting, so a class asserting the feature is on could otherwise be undercut by one turning it
/// off mid-run.
///
/// Sharing one fixture makes both problems structural rather than a matter of timing. Each test
/// still sets the feature state it depends on rather than inheriting whatever the previous one
/// left behind.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TokenedApiCollection : ICollectionFixture<AdminTokenAuthEndpointTests.TokenedApi>
{
    public const string Name = "TokenedApi";
}
