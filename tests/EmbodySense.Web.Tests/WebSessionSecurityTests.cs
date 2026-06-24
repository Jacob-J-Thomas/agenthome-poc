using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    public void IsAuthorized_allows_read_only_api_requests_without_token()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Get, "/api/status");

        Assert.False(session.RequiresToken(context.Request));
        Assert.True(session.IsAuthorized(context.Request));
    }

    [Fact]
    public void IsAuthorized_requires_token_for_api_mutations()
    {
        var session = new WebSessionSecurity("secret");
        var denied = CreateContext(HttpMethods.Post, "/api/messages");
        var allowed = CreateContext(HttpMethods.Post, "/api/messages");
        allowed.Request.Headers[WebSessionSecurity.HeaderName] = "secret";

        Assert.True(session.RequiresToken(denied.Request));
        Assert.False(session.IsAuthorized(denied.Request));
        Assert.True(session.IsAuthorized(allowed.Request));
    }

    [Fact]
    public void IsAuthorized_ignores_non_api_static_paths()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Post, "/styles.css");

        Assert.False(session.RequiresToken(context.Request));
        Assert.True(session.IsAuthorized(context.Request));
    }

    [Fact]
    public void IsAuthorized_rejects_non_local_host()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Get, "/api/status", "192.168.1.20");

        Assert.False(session.IsAuthorized(context.Request));
    }

    [Fact]
    public void IsAuthorized_rejects_remote_or_mismatched_origin()
    {
        var session = new WebSessionSecurity("secret");
        var remote = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        remote.Request.Headers.Origin = "http://example.com";
        var mismatchedPort = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        mismatchedPort.Request.Headers.Origin = "http://127.0.0.1:9999";

        Assert.False(session.IsAuthorized(remote.Request));
        Assert.False(session.IsAuthorized(mismatchedPort.Request));
    }

    [Fact]
    public void IsAuthorized_rejects_malformed_origin()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Get, "/api/status", "127.0.0.1:4378");
        context.Request.Headers.Origin = "not a url";

        Assert.False(session.IsAuthorized(context.Request));
    }

    [Fact]
    public void IsAuthorized_allows_local_origin_with_matching_port()
    {
        var session = new WebSessionSecurity("secret");
        var context = CreateContext(HttpMethods.Get, "/api/status", "localhost:4378");
        context.Request.Headers.Origin = "http://127.0.0.1:4378";

        Assert.True(session.IsAuthorized(context.Request));
    }

    [Fact]
    public async Task Middleware_rejects_unauthorized_request()
    {
        var context = CreateContext(HttpMethods.Post, "/api/messages");
        context.RequestServices = new ServiceCollection()
            .AddSingleton(new WebSessionSecurity("secret"))
            .BuildServiceProvider();
        var nextCalled = false;
        var middleware = new WebSessionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, context.RequestServices.GetRequiredService<WebSessionSecurity>());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("Forbidden EmbodySense web request.", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Middleware_allows_authorized_request()
    {
        var context = CreateContext(HttpMethods.Post, "/api/messages");
        context.Request.Headers[WebSessionSecurity.HeaderName] = "secret";
        context.RequestServices = new ServiceCollection()
            .AddSingleton(new WebSessionSecurity("secret"))
            .BuildServiceProvider();
        var nextCalled = false;
        var middleware = new WebSessionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, context.RequestServices.GetRequiredService<WebSessionSecurity>());

        Assert.True(nextCalled);
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
