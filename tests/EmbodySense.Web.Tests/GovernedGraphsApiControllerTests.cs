using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EmbodySense.Web.Tests;

public sealed class GovernedGraphsApiControllerTests
{
    [Fact]
    public void Graph_json_contract_round_trips_canonical_private_constructor_values_and_rejects_nested_unknown_members()
    {
        using var workspace = new TestWorkspace();
        var arguments = new[]
        {
            "--workdir", workspace.RootPath,
            "--port", GetFreePort().ToString(),
            "--model", "gpt-test",
            "--codex-path", "codex",
        };
        var runOptions = WebRunOptions.FromArguments(arguments);
        var services = new ServiceCollection();
        Program.ConfigureServices(services, runOptions);
        using var provider = services.BuildServiceProvider();
        var jsonOptions = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var ceiling = GovernedLoopAuthorityCeiling.Create([
            "org.embodysense/model-inference",
            "org.embodysense/triggers/time",
        ]);
        var reference = GovernedLoopRevisionReference.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            "scheduled-graph",
            "revision-1",
            new string('a', 64));

        var ceilingJson = JsonSerializer.Serialize(ceiling, jsonOptions);
        var referenceJson = JsonSerializer.Serialize(reference, jsonOptions);
        var restoredCeiling = JsonSerializer.Deserialize<GovernedLoopAuthorityCeiling>(ceilingJson, jsonOptions);
        var restoredReference = JsonSerializer.Deserialize<GovernedLoopRevisionReference>(referenceJson, jsonOptions);

        Assert.NotNull(restoredCeiling);
        Assert.Equal(ceiling.CapabilityIds, restoredCeiling.CapabilityIds);
        Assert.Equal(reference, restoredReference);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GovernedLoopAuthorityCeiling>(
            """{"capabilityIds":[],"actorId":"caller-selected"}""",
            jsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GovernedLoopRevisionReference>(
            """{"schemaVersion":1,"graphId":"scheduled-graph","revisionId":"revision-1","executableHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","authorityEvidence":"caller-selected"}""",
            jsonOptions));
    }
    [Fact]
    public async Task Graph_api_requires_local_session_and_initialized_workspace_then_projects_exact_catalog_and_read_status()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var unauthorized = await client.GetAsync("/api/governed-graphs/catalog");
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var beforeInitialization = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/catalog", token);
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token);
            var catalog = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/catalog", token);
            var missing = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/detail?graphId=missing-graph", token);
            var catalogJson = await catalog.Content.ReadAsStringAsync();
            var missingJson = await missing.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
            Assert.True(catalog.Headers.CacheControl?.NoStore == true);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var missingDocument = JsonDocument.Parse(missingJson);
            Assert.Equal("not-found", missingDocument.RootElement.GetProperty("status").GetString());
            using var document = JsonDocument.Parse(catalogJson);
            Assert.Equal("available", document.RootElement.GetProperty("status").GetString());
            var descriptors = document.RootElement.GetProperty("nodeDescriptors").EnumerateArray().ToArray();
            Assert.Contains(descriptors, descriptor => descriptor.GetProperty("descriptor").GetProperty("kind").GetString() == "trigger");
            Assert.Contains(descriptors, descriptor => descriptor.GetProperty("descriptor").GetProperty("kind").GetString() == "wait");
            Assert.DoesNotContain(workspace.RootPath, catalogJson, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }


    [Fact]
    public async Task Graph_mutation_contract_rejects_missing_content_and_caller_supplied_trusted_identity()
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

            var missing = await SendAsync(client, HttpMethod.Post, "/api/governed-graphs/mutate", token, null, includeNullBody: true);
            var callerTrust = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/governed-graphs/mutate",
                token,
                new
                {
                    operationId = "create-graph-with-caller-trust",
                    kind = "create-draft",
                    graphId = "graph-caller-trust",
                    expectedLifecycleStatus = "unknown",
                    expectedLifecycleVersion = 0,
                    expectedDraftRevision = (object?)null,
                    expectedPublishedRevision = (object?)null,
                    graphCandidate = (object?)null,
                    actorId = "caller-selected-actor",
                });
            var callerTrustJson = await callerTrust.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, callerTrust.StatusCode);
            Assert.Contains("actorId", callerTrustJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static Microsoft.AspNetCore.Builder.WebApplication CreateApp(
        TestWorkspace workspace,
        string codexPath,
        out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[]
        {
            "--workdir", workspace.RootPath,
            "--port", port.ToString(),
            "--model", "gpt-test",
            "--codex-path", codexPath,
        };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        builder.Services.AddSingleton(new WebAgentRuntimeHost(
            options,
            new WebApprovalCoordinator(),
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            workspace.ServerStatePath));
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
