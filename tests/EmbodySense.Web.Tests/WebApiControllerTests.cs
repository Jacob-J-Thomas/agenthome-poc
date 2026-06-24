using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EmbodySense.Web.Tests;

public sealed class WebApiControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Configured_app_serves_status_init_and_approval_endpoints()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var session = await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions);
            var before = await client.GetFromJsonAsync<WebStatus>("/api/status", JsonOptions);
            var rejectedInit = await client.PostAsJsonAsync("/api/workspace/init", new { }, JsonOptions);
            var initRequest = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/init");
            initRequest.Headers.Add(WebSessionSecurity.HeaderName, session!.Token);
            initRequest.Content = JsonContent.Create(new { }, options: JsonOptions);
            var initialized = await client.SendAsync(initRequest);
            var after = await initialized.Content.ReadFromJsonAsync<WebStatus>(JsonOptions);
            var approvals = await client.GetFromJsonAsync<WebPendingApproval[]>("/api/approvals/pending", JsonOptions);
            var missingApproval = new HttpRequestMessage(HttpMethod.Post, "/api/approvals/missing");
            missingApproval.Headers.Add(WebSessionSecurity.HeaderName, session.Token);
            missingApproval.Content = JsonContent.Create(new WebApprovalDecision(true, null), options: JsonOptions);
            var missingApprovalResponse = await client.SendAsync(missingApproval);

            Assert.False(before!.Initialized);
            Assert.Equal(HttpStatusCode.Forbidden, rejectedInit.StatusCode);
            Assert.True(initialized.IsSuccessStatusCode);
            Assert.True(after!.Initialized);
            Assert.Equal("web", after.Client);
            Assert.True(after.PrimaryClient);
            Assert.Equal(options.Url, after.Url);
            Assert.Contains("CLI remains supported", after.CliRole);
            Assert.Empty(approvals!);
            Assert.Equal(HttpStatusCode.NotFound, missingApprovalResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Message_endpoint_rejects_empty_message()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages");
            request.Headers.Add(WebSessionSecurity.HeaderName, token);
            request.Content = JsonContent.Create(new WebMessageRequest(" "), options: JsonOptions);

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Message is required.", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Message_endpoint_streams_error_when_workspace_is_not_initialized()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages");
            request.Headers.Add(WebSessionSecurity.HeaderName, token);
            request.Content = JsonContent.Create(new WebMessageRequest("hello"), options: JsonOptions);

            var response = await client.SendAsync(request);
            var line = (await response.Content.ReadAsStringAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Single();
            using var json = JsonDocument.Parse(line);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
            Assert.Contains("Workspace is not initialized", json.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static WebApplication CreateApp(string rootPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        options = WebRunOptions.FromArguments(["--workdir", rootPath, "--port", port.ToString()]);
        var builder = Program.CreateBuilder(["--workdir", rootPath, "--port", port.ToString()], options);
        return BuildApp(builder);
    }

    private static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
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
