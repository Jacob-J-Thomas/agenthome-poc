using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebGovernedLoopBackgroundLifetimeTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Hosted_lifetime_defers_until_workspace_initialization_then_retries_to_ready()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var before = await ReadStatusAsync(client);
            using var session = await client.GetAsync("/api/session");
            using var initialize = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/init");
            initialize.Headers.Add("Cookie", SessionCookie(session));
            initialize.Content = JsonContent.Create(new { }, options: _jsonOptions);
            using var initialized = await client.SendAsync(initialize);

            Assert.Equal(WebGovernedLoopBackgroundPosture.Unavailable, before.BackgroundPosture);
            Assert.True(initialized.IsSuccessStatusCode);
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Background_lifetime_remains_ready_after_the_only_browser_connection_disconnects()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
            using var session = await client.GetAsync("/api/session");
            var sessionCookie = SessionCookie(session);
            using var socket = new ClientWebSocket();
            socket.Options.Cookies = new CookieContainer();
            socket.Options.Cookies.SetCookies(new Uri(options.Url), sessionCookie);
            await socket.ConnectAsync(ToHubUri(options.Url), CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", CancellationToken.None);

            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Shutdown_reports_stopped_and_disposes_the_pinned_runtime_idempotently()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
            var runtimeHost = app.Services.GetRequiredService<WebAgentRuntimeHost>();
            await runtimeHost.SendMessageAsync("background runtime disposal", (_, _) => Task.CompletedTask);
            await WaitForLinesAsync(WebBackgroundLifetimeCodexExecutable.StartedPath(workspace), 1);
            await app.StopAsync();
            Assert.Equal(WebGovernedLoopBackgroundPosture.Stopped, runtimeHost.GetStatus().BackgroundPosture);
            await app.DisposeAsync();
            await app.DisposeAsync();

            Assert.Single(await File.ReadAllLinesAsync(WebBackgroundLifetimeCodexExecutable.StartedPath(workspace)));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static WebApplication CreateApp(string rootPath, string codexPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test", "--codex-path", codexPath };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static async Task<WebStatus> ReadStatusAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<WebStatus>("/api/status", _jsonOptions)
            ?? throw new InvalidOperationException("The Web status response was empty.");
    }

    private static async Task<WebStatus> WaitForPostureAsync(HttpClient client, WebGovernedLoopBackgroundPosture posture)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var status = await ReadStatusAsync(client);
            if (status.BackgroundPosture == posture)
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The Web background posture did not reach `{posture}`.");
    }

    private static async Task WaitForLinesAsync(string path, int count)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (File.Exists(path) && (await File.ReadAllLinesAsync(path)).Length >= count)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The tracked Codex process did not write {count} line(s) to `{path}`.");
    }

    private static string SessionCookie(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
    }

    private static Uri ToHubUri(string baseUrl)
    {
        var builder = new UriBuilder(baseUrl) { Scheme = Uri.UriSchemeWs, Path = "/hubs/session" };
        return builder.Uri;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
