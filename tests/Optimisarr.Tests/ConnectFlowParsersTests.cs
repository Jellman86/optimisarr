using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class PlexPinParserTests
{
    [Fact]
    public void Parses_a_created_pin()
    {
        const string json = """{ "id": 123456, "code": "AB12", "authToken": null }""";

        var pin = PlexPinParser.ParsePin(json);

        Assert.NotNull(pin);
        Assert.Equal(123456, pin!.Id);
        Assert.Equal("AB12", pin.Code);
    }

    [Fact]
    public void A_pin_without_an_auth_token_yet_returns_null_token()
    {
        Assert.Null(PlexPinParser.ParseAuthToken("""{ "id": 1, "code": "X", "authToken": null }"""));
    }

    [Fact]
    public void A_claimed_pin_returns_the_token()
    {
        Assert.Equal("tok-123", PlexPinParser.ParseAuthToken("""{ "id": 1, "code": "X", "authToken": "tok-123" }"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "code": "X" }""")]
    public void Bad_or_incomplete_pin_payloads_return_null(string json)
    {
        Assert.Null(PlexPinParser.ParsePin(json));
    }
}

public sealed class JellyfinQuickConnectParserTests
{
    [Fact]
    public void Parses_an_initiation()
    {
        const string json = """{ "Authenticated": false, "Secret": "s3cr3t", "Code": "123456" }""";

        var initiation = JellyfinQuickConnectParser.ParseInitiation(json);

        Assert.NotNull(initiation);
        Assert.Equal("123456", initiation!.Code);
        Assert.Equal("s3cr3t", initiation.Secret);
    }

    [Fact]
    public void Reads_the_authenticated_flag()
    {
        Assert.True(JellyfinQuickConnectParser.ParseAuthenticated("""{ "Authenticated": true }"""));
        Assert.False(JellyfinQuickConnectParser.ParseAuthenticated("""{ "Authenticated": false }"""));
        Assert.False(JellyfinQuickConnectParser.ParseAuthenticated("{}"));
    }

    [Fact]
    public void Reads_the_access_token()
    {
        Assert.Equal("jf-token", JellyfinQuickConnectParser.ParseAccessToken("""{ "AccessToken": "jf-token", "User": {} }"""));
        Assert.Null(JellyfinQuickConnectParser.ParseAccessToken("""{ "User": {} }"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("""{ "Code": "123456" }""")]
    public void Bad_or_incomplete_initiations_return_null(string json)
    {
        Assert.Null(JellyfinQuickConnectParser.ParseInitiation(json));
    }
}
