using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
            var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/approvals/pending");
            approvalsRequest.Headers.Add(WebSessionSecurity.HeaderName, session.Token);
            var approvalsResponse = await client.SendAsync(approvalsRequest);
            var approvals = await approvalsResponse.Content.ReadFromJsonAsync<WebPendingApproval[]>(JsonOptions);
            var missingApproval = new HttpRequestMessage(HttpMethod.Post, "/api/approvals/missing");
            missingApproval.Headers.Add(WebSessionSecurity.HeaderName, session.Token);
            missingApproval.Content = JsonContent.Create(new WebApprovalDecision(true, null), options: JsonOptions);
            var missingApprovalResponse = await client.SendAsync(missingApproval);

            Assert.False(before!.Initialized);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedInit.StatusCode);
            Assert.True(initialized.IsSuccessStatusCode);
            Assert.True(after!.Initialized);
            Assert.Equal("web", after.Client);
            Assert.True(after.PrimaryClient);
            Assert.Equal(options.Url, after.Url);
            Assert.Contains("CLI remains supported", after.CliRole);
            Assert.True(approvalsResponse.IsSuccessStatusCode);
            Assert.Empty(approvals!);
            Assert.Equal(HttpStatusCode.NotFound, missingApprovalResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Hub_negotiate_requires_session_token()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var rejected = await client.PostAsync("/hubs/session/negotiate?negotiateVersion=1", null);
            var accepted = await client.PostAsync($"/hubs/session/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);

            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.True(accepted.IsSuccessStatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Approval_decision_defaults_to_reject_when_approved_is_missing()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var coordinator = app.Services.GetRequiredService<WebApprovalCoordinator>();
            var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-default-reject"));
            await WaitForPendingAsync(coordinator);
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/approvals/req-default-reject");
            request.Headers.Add(WebSessionSecurity.HeaderName, token);
            request.Content = JsonContent.Create(new { }, options: JsonOptions);

            var response = await client.SendAsync(request);
            var approval = await approvalTask;

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.False(approval.Approved);
            Assert.Equal("Rejected in the localhost web client.", approval.Detail);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static AgentToolApprovalRequest CreateRequest(string id)
    {
        return new AgentToolApprovalRequest(
            id,
            "read",
            "workspace/shared/example.txt",
            @"C:\workspace\shared\example.txt",
            "read",
            "workspace/shared/**",
            "Needs approval.");
    }

    private static async Task WaitForPendingAsync(WebApprovalCoordinator coordinator)
    {
        for (var i = 0; i < 20; i++)
        {
            if (coordinator.GetPending().Count > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Approval request was not queued.");
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
