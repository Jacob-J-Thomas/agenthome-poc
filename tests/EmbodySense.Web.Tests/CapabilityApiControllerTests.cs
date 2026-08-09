using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

public sealed class CapabilityApiControllerTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Capability_catalog_api_requires_local_authentication_and_initialized_workspace_and_disables_caching()
    {
        using var workspace = new TestWorkspace();
        var facade = new StubCapabilityCatalogFacade();
        await using var app = CreateApp(workspace.RootPath, workspace.ServerStatePath, facade, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var rejected = await client.GetAsync("/api/capabilities");
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var uninitialized = await SendAsync(client, HttpMethod.Get, "/api/capabilities", token);
            var uninitializedDetail = await SendAsync(client, HttpMethod.Get, "/api/capabilities/detail?capabilityId=org.example%2Fruntime", token);
            var uninitializedPreview = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/preview", token, new
            {
                operationId = "web-preview-uninitialized",
                operation = "disable",
                capabilityId = "org.example/runtime",
                targetVersion = (string?)null
            });
            var uninitializedConfirmation = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/confirm", token, ConfirmationBody());
            var initialize = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { });
            Assert.True(initialize.StatusCode == HttpStatusCode.OK, await initialize.Content.ReadAsStringAsync());

            var response = await SendAsync(client, HttpMethod.Get, "/api/capabilities?maximumCount=20&cursor=org.example%2Fbefore", token);
            var catalog = await response.Content.ReadFromJsonAsync<CapabilityPostureCatalogResponse>(_jsonOptions);

            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, uninitialized.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, uninitializedDetail.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, uninitializedPreview.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, uninitializedConfirmation.StatusCode);
            Assert.Contains("workspace_not_initialized", await uninitialized.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
            Assert.Equal("available", catalog!.Status);
            Assert.Equal("org.example/before", facade.CatalogCursor);
            Assert.Equal(20, facade.CatalogMaximumCount);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Capability_lifecycle_api_accepts_only_bounded_selection_and_confirmation_shapes()
    {
        using var workspace = new TestWorkspace();
        var facade = new StubCapabilityCatalogFacade();
        await using var app = CreateApp(workspace.RootPath, workspace.ServerStatePath, facade, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var initialize = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { });
            Assert.True(initialize.StatusCode == HttpStatusCode.OK, await initialize.Content.ReadAsStringAsync());

            var preview = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/preview", token, new
            {
                operationId = "web-preview",
                operation = "disable",
                capabilityId = "org.example/runtime-lifecycle",
                targetVersion = (string?)null
            });
            var malformed = await SendRawJsonAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/preview", token, "{\"operationId\":\"forged\",\"operation\":\"disable\",\"capabilityId\":\"org.example/runtime-lifecycle\",\"targetDescriptor\":{}}");
            var confirm = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/confirm", token, new
            {
                operationId = "web-preview",
                operation = "disable",
                capabilityId = "org.example/runtime-lifecycle",
                targetVersion = (string?)null,
                baselineCatalogRevision = 8,
                baselineActivationRevision = 3,
                lifecycleRevision = 2,
                dependentSetRevision = 5,
                dependentSetHash = "sha256:dependents",
                previewHash = "sha256:preview",
                confirmed = true
            });

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.True(preview.Headers.CacheControl?.NoStore == true);
            Assert.Equal("web-preview", facade.PreviewInput!.OperationId);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            Assert.True(confirm.Headers.CacheControl?.NoStore == true);
            Assert.True(facade.ConfirmationInput!.Confirmed);
            Assert.Equal("sha256:preview", facade.ConfirmationInput.PreviewHash);
            Assert.DoesNotContain("targetDescriptor", await preview.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("artifactDigest", await preview.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Capability_api_projects_stable_read_preview_and_mutation_failures()
    {
        using var workspace = new TestWorkspace();
        var facade = new StubCapabilityCatalogFacade();
        await using var app = CreateApp(workspace.RootPath, workspace.ServerStatePath, facade, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var initialize = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { });
            Assert.True(initialize.StatusCode == HttpStatusCode.OK, await initialize.Content.ReadAsStringAsync());

            facade.CatalogResponse = new CapabilityPostureCatalogResponse("invalid", null, [], null, new CapabilityPostureError("invalid", "Invalid."));
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Get, "/api/capabilities", token)).StatusCode);
            facade.CatalogResponse = new CapabilityPostureCatalogResponse("unavailable", null, [], null, new CapabilityPostureError("unavailable", "Unavailable."));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(client, HttpMethod.Get, "/api/capabilities", token)).StatusCode);
            facade.CatalogResponse = new CapabilityPostureCatalogResponse("recovered", 8, [], null, null);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/capabilities", token)).StatusCode);

            foreach (var (status, expected) in new[]
            {
                ("available", HttpStatusCode.OK),
                ("recovered", HttpStatusCode.OK),
                ("invalid", HttpStatusCode.BadRequest),
                ("not-found", HttpStatusCode.NotFound),
                ("unavailable", HttpStatusCode.ServiceUnavailable)
            })
            {
                facade.PostureResponse = new CapabilityPostureResponse(status, null, new CapabilityPostureError(status, "Bounded."));
                Assert.Equal(expected, (await SendAsync(client, HttpMethod.Get, "/api/capabilities/detail?capabilityId=org.example%2Fruntime", token)).StatusCode);
            }

            foreach (var (status, expected) in new[]
            {
                ("invalid", HttpStatusCode.BadRequest),
                ("not-found", HttpStatusCode.NotFound),
                ("ambiguous", HttpStatusCode.Conflict),
                ("conflict", HttpStatusCode.Conflict),
                ("unavailable", HttpStatusCode.ServiceUnavailable)
            })
            {
                facade.PreviewResponse = new CapabilityLifecyclePreviewResponse(status, null, new CapabilityPostureError(status, "Bounded."));
                var response = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/preview", token, new
                {
                    operationId = "web-preview-status",
                    operation = "disable",
                    capabilityId = "org.example/runtime",
                    targetVersion = (string?)null
                });
                Assert.Equal(expected, response.StatusCode);
            }

            foreach (var (status, expected) in new[]
            {
                ("invalid", HttpStatusCode.BadRequest),
                ("not-found", HttpStatusCode.NotFound),
                ("conflict", HttpStatusCode.Conflict),
                ("blocked", HttpStatusCode.Conflict),
                ("ambiguous", HttpStatusCode.Conflict),
                ("unavailable", HttpStatusCode.ServiceUnavailable)
            })
            {
                facade.MutationResponse = new CapabilityLifecycleMutationResponse(status, false, null, null, null, false, "Bounded.");
                var response = await SendAsync(client, HttpMethod.Post, "/api/capabilities/lifecycle/confirm", token, ConfirmationBody());
                Assert.Equal(expected, response.StatusCode);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static object ConfirmationBody() => new
    {
        operationId = "web-preview-status",
        operation = "disable",
        capabilityId = "org.example/runtime",
        targetVersion = (string?)null,
        baselineCatalogRevision = 8,
        baselineActivationRevision = 3,
        lifecycleRevision = 2,
        dependentSetRevision = 5,
        dependentSetHash = "sha256:dependents",
        previewHash = "sha256:preview",
        confirmed = true
    };

    private static WebApplication CreateApp(string rootPath, string trustRootPath, ICapabilityCatalogFacade facade, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test" };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        builder.Services.AddSingleton<ICapabilityCatalogFacade>(facade);
        builder.Services.AddSingleton(new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRootPath)));
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawJsonAsync(HttpClient client, HttpMethod method, string path, string token, string json)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubCapabilityCatalogFacade : ICapabilityCatalogFacade
    {
        public string? CatalogCursor { get; private set; }

        public int CatalogMaximumCount { get; private set; }

        public CapabilityLifecycleSelectionInput? PreviewInput { get; private set; }

        public CapabilityLifecycleConfirmationInput? ConfirmationInput { get; private set; }

        public CapabilityPostureCatalogResponse CatalogResponse { get; set; } = new("available", 8, [], null, null);

        public CapabilityPostureResponse PostureResponse { get; set; } = new("not-found", null, new CapabilityPostureError("capability_posture_unavailable", "No matching capability."));

        public CapabilityLifecyclePreviewResponse? PreviewResponse { get; set; }

        public CapabilityLifecycleMutationResponse? MutationResponse { get; set; }

        public Task<CapabilityPostureCatalogResponse> ReadCatalogAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            CatalogCursor = startAfterId;
            CatalogMaximumCount = maximumCount;
            return Task.FromResult(CatalogResponse);
        }

        public Task<CapabilityPostureResponse> ReadAsync(string capabilityId, CancellationToken cancellationToken = default) => Task.FromResult(PostureResponse);

        public Task<CapabilityLifecyclePreviewResponse> PreviewAsync(CapabilityLifecycleSelectionInput input, CancellationToken cancellationToken = default)
        {
            PreviewInput = input;
            if (PreviewResponse is not null)
            {
                return Task.FromResult(PreviewResponse);
            }
            var preview = new CapabilityLifecyclePreviewSnapshot(input.OperationId, input.Operation, input.CapabilityId, input.TargetVersion, 8, 3, 2, 5, "sha256:dependents", "sha256:preview", false, false, [], "Ready.");
            return Task.FromResult(new CapabilityLifecyclePreviewResponse("ready", preview, null));
        }

        public Task<CapabilityLifecycleMutationResponse> ConfirmAsync(CapabilityLifecycleConfirmationInput input, CancellationToken cancellationToken = default)
        {
            ConfirmationInput = input;
            if (MutationResponse is not null)
            {
                return Task.FromResult(MutationResponse);
            }
            return Task.FromResult(new CapabilityLifecycleMutationResponse("applied", true, null, new CapabilityLifecycleMutationStateSnapshot(input.CapabilityId, "1.0.0", false, false, 3, DateTimeOffset.Parse("2026-08-09T12:00:00Z")), 3, false, "Applied."));
        }
    }
}
