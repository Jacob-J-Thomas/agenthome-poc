using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class GovernedSchedulesControllerTests
{
    [Fact]
    public async Task Schedule_api_projects_invalid_identifiers_and_missing_graphs_through_the_authenticated_host()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token)).StatusCode);

            var invalidRead = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/detail?scheduleId=INVALID", token);
            var invalidCreate = await SendAsync(client, HttpMethod.Post, "/api/governed-schedules/create", token, CreateInput(operationId: ""));
            var missingGraph = await SendAsync(client, HttpMethod.Post, "/api/governed-schedules/create", token, CreateInput());

            Assert.Equal(HttpStatusCode.BadRequest, invalidRead.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
            Assert.True(missingGraph.StatusCode == HttpStatusCode.NotFound, await missingGraph.Content.ReadAsStringAsync());

            using var invalidReadDocument = JsonDocument.Parse(await invalidRead.Content.ReadAsStringAsync());
            using var invalidCreateDocument = JsonDocument.Parse(await invalidCreate.Content.ReadAsStringAsync());
            using var missingGraphDocument = JsonDocument.Parse(await missingGraph.Content.ReadAsStringAsync());
            Assert.Equal("invalid", invalidReadDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("invalid", invalidCreateDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("not-found", missingGraphDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Schedule_api_maps_runtime_acquisition_failures_to_bounded_service_unavailable()
    {
        using var workspace = new TestWorkspace();
        var missingCodexPath = workspace.File("missing-codex");
        await using var app = CreateApp(workspace, missingCodexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token)).StatusCode);

            var timeZones = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/time-zones", token);
            var read = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/detail?scheduleId=INVALID", token);
            var create = await SendAsync(client, HttpMethod.Post, "/api/governed-schedules/create", token, CreateInput());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, timeZones.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
            var readBody = await read.Content.ReadAsStringAsync();
            var createBody = await create.Content.ReadAsStringAsync();
            Assert.Contains("governed_schedule_runtime_unavailable", readBody, StringComparison.Ordinal);
            Assert.Contains("governed_schedule_runtime_unavailable", createBody, StringComparison.Ordinal);
            Assert.DoesNotContain(missingCodexPath, readBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(missingCodexPath, createBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static object CreateInput(string operationId = "schedule-missing-graph")
        => new
        {
            operationId,
            graphId = "missing-graph",
            revisionId = "missing-revision",
            expectedGraphLifecycleVersion = 1,
            expectedAuthorityPreviewHash = (string?)null,
            recurrenceKind = "once",
            firstLocalOccurrence = "2030-01-01T00:00:00",
            fixedIntervalSeconds = (long?)null,
            timeZoneId = "UTC",
            invalidLocalTime = "skip",
            ambiguousLocalTime = "earlier-utc",
            misfireKind = "skip",
            catchUpLimit = 0,
            overlap = "skip",
            priority = "normal",
            enabled = true,
        };

    private static WebApplication CreateApp(TestWorkspace workspace, string codexPath, out WebRunOptions options)
    {
        var arguments = new[]
        {
            "--workdir", workspace.RootPath,
            "--port", GetFreePort().ToString(),
            "--model", "gpt-test",
            "--codex-path", codexPath,
        };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        var approvals = new WebApprovalCoordinator();
        builder.Services.AddSingleton(new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            conversationPublicationObserver: null,
            runtimeStatus => EmbodySense.Core.Startup.Runtime.AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                approvals,
                workspace.ServerStatePath,
                runtimeStatus)));
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
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
