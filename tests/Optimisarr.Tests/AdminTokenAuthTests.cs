using Microsoft.AspNetCore.Http;
using Optimisarr.Api.Security;

namespace Optimisarr.Tests;

public sealed class AdminTokenAuthTests
{
    [Fact]
    public void Auth_is_required_only_when_a_token_is_configured()
    {
        Assert.False(AdminTokenAuth.Required(null));
        Assert.False(AdminTokenAuth.Required(""));
        Assert.False(AdminTokenAuth.Required("   "));
        Assert.True(AdminTokenAuth.Required("secret"));
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/ready")]
    [InlineData("/api/auth/status")]
    public void Health_readiness_and_auth_status_are_open_paths(string path)
    {
        Assert.True(AdminTokenAuth.IsOpenPath(path));
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/cleanup")]
    [InlineData("/api/settings/export")]
    [InlineData("/api/jobs/1/replace")]
    [InlineData("/hubs/jobs")]
    public void Api_and_hub_paths_are_protected(string path)
    {
        Assert.True(AdminTokenAuth.IsProtectedPath(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/assets/app.js")]
    [InlineData("/favicon.png")]
    public void Static_spa_assets_are_not_protected_by_the_admin_token_middleware(string path)
    {
        Assert.False(AdminTokenAuth.IsProtectedPath(path));
    }

    [Fact]
    public void Bearer_token_authorizes_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer secret";

        Assert.True(AdminTokenAuth.IsAuthorized(context.Request, "secret"));
    }

    [Fact]
    public void Derived_session_cookie_authorizes_request_without_exposing_the_admin_token()
    {
        var issueContext = new DefaultHttpContext();
        AdminTokenAuth.SetSessionCookie(issueContext.Response, "secret", secure: true);
        var setCookie = issueContext.Response.Headers.SetCookie.ToString();
        var cookiePair = setCookie.Split(';', 2)[0];

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = cookiePair;

        Assert.DoesNotContain("secret", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(AdminTokenAuth.IsAuthorized(requestContext.Request, "secret"));
    }

    [Fact]
    public void Explicit_wrong_bearer_is_rejected_even_when_a_valid_session_cookie_exists()
    {
        var issueContext = new DefaultHttpContext();
        AdminTokenAuth.SetSessionCookie(issueContext.Response, "secret", secure: false);
        var cookiePair = issueContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = cookiePair;
        requestContext.Request.Headers.Authorization = "Bearer wrong";

        Assert.False(AdminTokenAuth.IsAuthorized(requestContext.Request, "secret"));
    }

    [Fact]
    public void Query_token_authorizes_signalr_hub_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/jobs";
        context.Request.QueryString = new QueryString("?access_token=secret");

        Assert.True(AdminTokenAuth.IsAuthorized(context.Request, "secret"));
    }

    [Fact]
    public void Query_token_does_not_authorize_api_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/settings/export";
        context.Request.QueryString = new QueryString("?access_token=secret");

        Assert.False(AdminTokenAuth.IsAuthorized(context.Request, "secret"));
    }

    [Fact]
    public void Wrong_token_does_not_authorize_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer wrong";

        Assert.False(AdminTokenAuth.IsAuthorized(context.Request, "secret"));
    }
}
