using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

public sealed class LoopOperationsApiControllerTests
{
    [Fact]
    public async Task Operational_api_requires_local_session_and_initialized_workspace_then_projects_canonical_posture()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace.RootPath, workspace.ServerStatePath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var unauthorized = await client.GetAsync("/api/loop-operations/posture");
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var beforeInitialization = await SendAsync(client, HttpMethod.Get, "/api/loop-operations/posture", token);
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token);
            var available = await SendAsync(client, HttpMethod.Get, "/api/loop-operations/posture?maximumQueueEntries=4&maximumSchedules=3&maximumWakes=2&maximumRuns=1", token);
            var availableJson = await available.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal(HttpStatusCode.OK, available.StatusCode);
            Assert.True(available.Headers.CacheControl?.NoStore == true);
            using var document = JsonDocument.Parse(availableJson);
            Assert.Equal("available", document.RootElement.GetProperty("status").GetString());
            var snapshot = document.RootElement.GetProperty("snapshot");
            Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(64, snapshot.GetProperty("controlAuthorityEvidenceHash").GetString()!.Length);
            Assert.Empty(snapshot.GetProperty("queue").GetProperty("items").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("schedules").GetProperty("items").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("wakes").GetProperty("items").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("runs").GetProperty("items").EnumerateArray());
            Assert.DoesNotContain("payload", availableJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(workspace.RootPath, availableJson, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Operational_control_accepts_only_exact_shared_facade_contract_and_rejects_unknown_kinds()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace.RootPath, workspace.ServerStatePath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token)).StatusCode);

            var missing = await SendAsync(client, HttpMethod.Post, "/api/loop-operations/control", token, null, includeNullBody: true);
            var unknown = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/loop-operations/control",
                token,
                new
                {
                    operationId = "operation-unknown-kind",
                    kind = "invented-control",
                    targetId = "target-1",
                    expectedRevision = 1,
                    expectedEvidenceHash = new string('a', 64),
                    expectedAuthorityEvidenceHash = new string('b', 64),
                    maximumBatchItems = 1,
                });
            var unknownJson = await unknown.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using var document = JsonDocument.Parse(unknownJson);
            Assert.Equal("invalid", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("operational-control-kind-invalid", document.RootElement.GetProperty("reasonCode").GetString());
            Assert.DoesNotContain("actor", unknownJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", unknownJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("surface", unknownJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static Microsoft.AspNetCore.Builder.WebApplication CreateApp(
        string rootPath,
        string trustRootPath,
        string codexPath,
        out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[]
        {
            "--workdir", rootPath,
            "--port", port.ToString(),
            "--model", "gpt-test",
            "--codex-path", codexPath,
        };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        builder.Services.AddSingleton(new WebAgentRuntimeHost(
            options,
            new WebApprovalCoordinator(),
            WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRootPath),
            trustRootPath));
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        object? body = null,
        bool includeNullBody = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (body is not null || includeNullBody)
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
