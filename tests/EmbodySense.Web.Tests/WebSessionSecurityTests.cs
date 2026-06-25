using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebSessionSecurityTests
{
    [Fact]
    public void Constructor_creates_session_token()
    {
        var session = new WebSessionSecurity();

        Assert.Equal(64, session.Token.Length);
        Assert.True(session.Token.All(Uri.IsHexDigit));
    }

    [Fact]
    public void HasValidToken_accepts_header_or_hub_query_token()
    {
        var session = new WebSessionSecurity("secret");
        var header = CreateContext(HttpMethods.Post, "/api/workspace/init");
        header.Request.Headers[WebSessionSecurity.HeaderName] = "secret";
        var query = CreateContext(HttpMethods.Get, "/hubs/session");
        query.Request.QueryString = QueryString.Create("access_token", "secret");
        var apiQuery = CreateContext(HttpMethods.Get, "/api/configuration");
        apiQuery.Request.QueryString = QueryString.Create("access_token", "secret");
        var denied = CreateContext(HttpMethods.Post, "/api/workspace/init");

        Assert.True(session.HasValidToken(header.Request));
        Assert.True(session.HasValidToken(query.Request));
        Assert.False(session.HasValidToken(apiQuery.Request));
        Assert.False(session.HasValidToken(denied.Request));
    }

    [Fact]
    public void IsHostAllowed_rejects_non_local_host()
    {
        var session = new WebSessionSecurity("secret");

        Assert.False(session.IsHostAllowed(HostString.FromUriComponent("192.168.1.20")));
        Assert.True(session.IsHostAllowed(HostString.FromUriComponent("127.0.0.1")));
        Assert.True(session.IsHostAllowed(HostString.FromUriComponent("[::1]")));
    }

    [Fact]
    public void IsOriginAllowed_rejects_remote_or_mismatched_origin()
    {
        var session = new WebSessionSecurity("secret");
        var remote = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        remote.Request.Headers.Origin = "http://example.com";
        var mismatchedPort = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        mismatchedPort.Request.Headers.Origin = "http://127.0.0.1:9999";

        Assert.False(session.IsOriginAllowed(remote.Request));
        Assert.False(session.IsOriginAllowed(mismatchedPort.Request));
    }

    [Fact]
    public void IsOriginAllowed_rejects_malformed_origin()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        context.Request.Headers.Origin = "not a url";

        Assert.False(session.IsOriginAllowed(context.Request));
    }

    [Fact]
    public void IsOriginAllowed_allows_missing_origin_or_matching_local_origin()
    {
        var session = new WebSessionSecurity("secret");
        var missing = CreateContext(HttpMethods.Get, "/api/status", "localhost:4378");
        var matching = CreateContext(HttpMethods.Get, "/api/status", "localhost:4378");
        matching.Request.Headers.Origin = "http://127.0.0.1:4378";

        Assert.True(session.IsOriginAllowed(missing.Request));
        Assert.True(session.IsOriginAllowed(matching.Request));
    }

    private static DefaultHttpContext CreateContext(string method, string path, string host = "127.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = HostString.FromUriComponent(host);
        context.Response.Body = new MemoryStream();
        return context;
    }
}
