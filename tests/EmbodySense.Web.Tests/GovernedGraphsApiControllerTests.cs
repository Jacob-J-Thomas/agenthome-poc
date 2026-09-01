using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles.Models;
using EmbodySense.Core.Startup.Loops.Schedules.Models;
using EmbodySense.Core.Startup.Runtime;
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
    public async Task Governed_schedule_api_requires_authentication_and_rejects_forged_server_owned_fields()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var unauthorized = await client.GetAsync("/api/governed-schedules/detail?scheduleId=schedule-1");
            var unauthorizedTimeZones = await client.GetAsync("/api/governed-schedules/time-zones");
            var beforeInitialization = await SendAsync(client, HttpMethod.Post, "/api/governed-schedules/create", token);
            var beforeInitializationTimeZones = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/time-zones", token);
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token);
            var timeZones = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/time-zones", token);
            var missing = await SendAsync(client, HttpMethod.Post, "/api/governed-schedules/create", token);
            var missingSchedule = await SendAsync(client, HttpMethod.Get, "/api/governed-schedules/detail?scheduleId=schedule-missing", token);
            var forged = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/governed-schedules/create",
                token,
                new
                {
                    operationId = "schedule-forged-owner",
                    graphId = "scheduled-graph",
                    revisionId = "revision-1",
                    expectedGraphLifecycleVersion = 1,
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
                    actorId = "caller-selected",
                });

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedTimeZones.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitializationTimeZones.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal(HttpStatusCode.OK, timeZones.StatusCode);
            using var timeZonesDocument = JsonDocument.Parse(await timeZones.Content.ReadAsStringAsync());
            Assert.Equal("available", timeZonesDocument.RootElement.GetProperty("status").GetString());
            Assert.Contains(
                timeZonesDocument.RootElement.GetProperty("timeZones").EnumerateArray(),
                item => item.GetProperty("id").GetString() == "UTC");
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missingSchedule.StatusCode);
            Assert.True(missingSchedule.Headers.CacheControl?.NoStore == true);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void Governed_schedule_public_projection_excludes_authority_payload_and_operational_evidence()
    {
        var response = new GovernedLoopScheduleAuthoringResponse(
            "ready",
            "schedule-operation",
            "Canonical schedule is current.",
            new GovernedLoopScheduleAuthoringSnapshot(
                "schedule-visible",
                "graph-visible",
                "revision-visible",
                true,
                7,
                new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero),
                "daily",
                new DateTime(2030, 1, 1, 6, 0, 0, DateTimeKind.Unspecified),
                null,
                "UTC",
                "skip",
                "earlier-utc",
                "skip",
                0,
                "skip",
                "normal"),
            null);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("schedule-visible", json, StringComparison.Ordinal);
        Assert.DoesNotContain("authorityProfile", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pendingDelivery", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidenceHash", json, StringComparison.OrdinalIgnoreCase);
    }

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
            var detailBeforeInitialization = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/detail?graphId=before-init", token);
            var mutationBeforeInitialization = await SendAsync(client, HttpMethod.Post, "/api/governed-graphs/mutate", token);
            var preparationBeforeInitialization = await SendAsync(client, HttpMethod.Post, "/api/governed-graphs/invocation-preparation", token);
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token);
            var catalog = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/catalog", token);
            var missingRetryPreview = await SendAsync(client, HttpMethod.Post, "/api/governed-graphs/retry-preview", token);
            var missingPreparationRequest = await SendAsync(client, HttpMethod.Post, "/api/governed-graphs/invocation-preparation", token);
            var missingPreparation = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/governed-graphs/invocation-preparation",
                token,
                new { graphId = "missing-graph", revisionId = "missing-revision" });
            var retryPreview = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/governed-graphs/retry-preview",
                token,
                new
                {
                    policyId = "retry-infer",
                    nodeId = "infer",
                    failureClasses = new[] { "retryable-no-effect" },
                    serverCodes = Array.Empty<string>(),
                    maximumAttempts = 3,
                    perAttemptTimeoutMilliseconds = 1_000,
                    maximumElapsedMilliseconds = 10_000,
                    backoffStrategy = "fixed",
                    initialDelayMilliseconds = 250,
                    maximumDelayMilliseconds = 250,
                    jitterStrategy = "none",
                    maximumJitterMilliseconds = 0,
                    maximumTokens = 3_000,
                    maximumToolCalls = (int?)null,
                    maximumCostMicrounits = (long?)null,
                    maximumCostCurrency = (string?)null,
                    maximumResourceUnits = (int?)null,
                });
            var invalidRetryPreview = await SendAsync(
                client,
                HttpMethod.Post,
                "/api/governed-graphs/retry-preview",
                token,
                new
                {
                    policyId = "retry-infer",
                    nodeId = "infer",
                    failureClasses = new[] { "retryable-no-effect" },
                    serverCodes = Array.Empty<string>(),
                    maximumAttempts = 9,
                    perAttemptTimeoutMilliseconds = 1_000,
                    maximumElapsedMilliseconds = 10_000,
                    backoffStrategy = "fixed",
                    initialDelayMilliseconds = 250,
                    maximumDelayMilliseconds = 250,
                    jitterStrategy = "none",
                    maximumJitterMilliseconds = 0,
                    maximumTokens = (long?)null,
                    maximumToolCalls = (int?)null,
                    maximumCostMicrounits = (long?)null,
                    maximumCostCurrency = (string?)null,
                    maximumResourceUnits = (int?)null,
                });
            var missing = await SendAsync(client, HttpMethod.Get, "/api/governed-graphs/detail?graphId=missing-graph", token);
            var catalogJson = await catalog.Content.ReadAsStringAsync();
            var retryPreviewJson = await retryPreview.Content.ReadAsStringAsync();
            var missingJson = await missing.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, detailBeforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, mutationBeforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, preparationBeforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.True(catalog.StatusCode == HttpStatusCode.OK, catalogJson);
            Assert.Equal(HttpStatusCode.BadRequest, missingRetryPreview.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missingPreparationRequest.StatusCode);
            Assert.Equal(HttpStatusCode.OK, missingPreparation.StatusCode);
            Assert.True(retryPreview.StatusCode == HttpStatusCode.OK, retryPreviewJson);
            Assert.Equal(HttpStatusCode.BadRequest, invalidRetryPreview.StatusCode);
            Assert.True(catalog.Headers.CacheControl?.NoStore == true);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var missingDocument = JsonDocument.Parse(missingJson);
            Assert.Equal("not-found", missingDocument.RootElement.GetProperty("status").GetString());
            using var document = JsonDocument.Parse(catalogJson);
            Assert.Equal("available", document.RootElement.GetProperty("status").GetString());
            var modelProfiles = document.RootElement.GetProperty("modelProfiles");
            Assert.Equal("available", modelProfiles.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, modelProfiles.GetProperty("defaultProfileId").ValueKind);
            var modelProfile = Assert.Single(modelProfiles.GetProperty("profiles").EnumerateArray());
            Assert.Equal("org.embodysense/model-profile/codex", modelProfile.GetProperty("profileId").GetString());
            Assert.Equal("adapterunavailable", modelProfile.GetProperty("availabilityReason").GetString());
            Assert.Equal(JsonValueKind.Null, modelProfile.GetProperty("recommendedExactPolicy").ValueKind);
            Assert.Equal(JsonValueKind.Null, modelProfile.GetProperty("exactProfilePin").ValueKind);
            Assert.Single(modelProfiles.GetProperty("profiles").EnumerateArray());
            var retryPolicies = document.RootElement.GetProperty("retryPolicies");
            Assert.Equal(8, retryPolicies.GetProperty("maximumAttempts").GetInt32());
            Assert.Contains("retryable-no-effect", retryPolicies.GetProperty("failureClasses").EnumerateArray().Select(item => item.GetString()));
            var descriptors = document.RootElement.GetProperty("nodeDescriptors").EnumerateArray().ToArray();
            Assert.Contains(descriptors, descriptor => descriptor.GetProperty("descriptor").GetProperty("kind").GetString() == "trigger");
            Assert.Contains(descriptors, descriptor => descriptor.GetProperty("descriptor").GetProperty("kind").GetString() == "wait");
            Assert.DoesNotContain(workspace.RootPath, catalogJson, StringComparison.Ordinal);
            using var retryDocument = JsonDocument.Parse(retryPreviewJson);
            Assert.Equal("valid", retryDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("retry-infer", retryDocument.RootElement.GetProperty("policy").GetProperty("policyId").GetString());
            Assert.Matches("^[0-9a-f]{64}$", retryDocument.RootElement.GetProperty("policy").GetProperty("contentHash").GetString());
            Assert.True(retryDocument.RootElement.GetProperty("preview").GetProperty("currentAdmissionStillRequired").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Model_profile_api_requires_initialization_then_projects_available_and_invalid_public_requests()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var jsonOptions = app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
            var beforeInitialization = await SendAsync(client, HttpMethod.Get, "/api/model-profiles", token);
            var previewBeforeInitialization = await SendJsonAsync(client, "/api/model-profiles/preview", token, CreateModelProfilePreviewInput(), jsonOptions);
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token);
            var available = await SendAsync(client, HttpMethod.Get, "/api/model-profiles?maximumCount=1", token);
            var invalidPage = await SendAsync(client, HttpMethod.Get, "/api/model-profiles?maximumCount=0", token);
            var ineligiblePreview = await SendJsonAsync(client, "/api/model-profiles/preview", token, CreateModelProfilePreviewInput(), jsonOptions);
            var invalidPreview = await SendJsonAsync(client, "/api/model-profiles/preview", token, CreateModelProfilePreviewInput(roleId: string.Empty), jsonOptions);

            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, previewBeforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal(HttpStatusCode.OK, available.StatusCode);
            Assert.True(available.Headers.CacheControl?.NoStore == true);
            Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ineligiblePreview.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidPreview.StatusCode);
            using var availableDocument = JsonDocument.Parse(await available.Content.ReadAsStringAsync());
            Assert.Equal("available", availableDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("adapterunavailable", Assert.Single(availableDocument.RootElement.GetProperty("profiles").EnumerateArray()).GetProperty("availabilityReason").GetString());
            using var ineligiblePreviewDocument = JsonDocument.Parse(await ineligiblePreview.Content.ReadAsStringAsync());
            Assert.Equal("ineligible", ineligiblePreviewDocument.RootElement.GetProperty("status").GetString());
            using var invalidPreviewDocument = JsonDocument.Parse(await invalidPreview.Content.ReadAsStringAsync());
            Assert.Equal("invalid", invalidPreviewDocument.RootElement.GetProperty("status").GetString());
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
        var approvalCoordinator = new WebApprovalCoordinator();
        builder.Services.AddSingleton(new WebAgentRuntimeHost(
            options,
            approvalCoordinator,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            conversationPublicationObserver: null,
            runtimeStatus => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                approvalCoordinator,
                workspace.ServerStatePath,
                runtimeStatus)));
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

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        string path,
        string token,
        object body,
        JsonSerializerOptions jsonOptions)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        request.Content = JsonContent.Create(body, options: jsonOptions);
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

    private static ModelProfileRoutingPreviewInput CreateModelProfilePreviewInput(IReadOnlyList<string>? authoredInputDataClasses = null, string roleId = "default")
    {
        Assert.True(CapabilityId.TryParse(BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, out var profileId, out _));
        Assert.True(CapabilityDataClass.TryParse("public", out var publicDataClass, out _));
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            localOnly: false,
            CapabilityEgressMode.None,
            [],
            [publicDataClass!],
            ["local"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var requirements = GovernedModelProfileRequirements.Create(
            1,
            [GovernedModelModality.Text],
            [],
            1,
            1,
            privacy,
            GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded));
        var policy = GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], requirements);
        return new ModelProfileRoutingPreviewInput(policy, roleId, "provider-inference", authoredInputDataClasses);
    }
}
