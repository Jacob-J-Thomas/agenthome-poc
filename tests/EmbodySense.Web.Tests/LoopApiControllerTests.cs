using EmbodySense.Web;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

public sealed class LoopApiControllerTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Loop_api_enforces_authentication_initialization_and_system_loop_lock()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var rejected = await client.GetAsync("/api/loops");
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var uninitializedCatalog = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var uninitializedCreate = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "create-before-init", definition = CreateFirstSaveDefinition() });
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { });
            var catalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var catalog = (await catalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(_jsonOptions))!;
            var systemGet = await SendAsync(client, HttpMethod.Get, "/api/loops/default-conversation", token);
            var systemJson = await systemGet.Content.ReadAsStringAsync();
            var systemDefinition = JsonSerializer.Deserialize<SystemLoopDefinitionSnapshot>(systemJson, _jsonOptions)!;
            var canonicalSystemDefinition = LoopDefinition.CreateDefaultConversation();
            var malformedGet = await SendAsync(client, HttpMethod.Get, "/api/loops/INVALID%20ID", token);
            var systemUpdate = await SendAsync(client, HttpMethod.Put, "/api/loops/default-conversation", token, CreateUpdateBody(null, "system-update", "System loop"));
            var systemDelete = await SendAsync(client, HttpMethod.Delete, "/api/loops/default-conversation", token, new { expectedDefinitionVersion = 1, operationId = "system-delete" });

            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, uninitializedCatalog.StatusCode);
            Assert.Contains("workspace_not_initialized", await uninitializedCatalog.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, uninitializedCreate.StatusCode);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            Assert.True(catalogResponse.Headers.CacheControl?.NoStore == true);
            Assert.Equal("default-conversation", catalog.SystemDefault.Id);
            Assert.Empty(catalog.CustomDefinitions);
            Assert.Equal(1, catalog.DraftTemplate.SchemaVersion);
            Assert.Equal(catalog.RoleId, catalog.DraftTemplate.RoleId);
            Assert.Equal("Untitled loop", catalog.DraftTemplate.Definition.DisplayName);
            Assert.Null(Assert.Single(catalog.DraftTemplate.Definition.InferenceSteps).Id);
            Assert.Equal(LoopContextPolicyMode.Inherit, catalog.DraftTemplate.Definition.InferenceSteps.Single().ContextPolicy.Mode);
            Assert.Equal(
                [LoopCapabilityIds.ConversationTurn, LoopCapabilityIds.ConversationHistory, LoopCapabilityIds.AgentContext, LoopCapabilityIds.ProviderInference, LoopCapabilityIds.WorkspaceCommand, LoopCapabilityIds.ApprovalRequest, LoopCapabilityIds.AuditWrite],
                catalog.SystemDefault.CapabilityIds);
            Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], catalog.Tools.CustomAssignable);
            Assert.Equal(LoopCustomToolAuthorityCeiling.WorkspaceReadOnly, catalog.Tools.CustomAuthorityCeiling);
            Assert.Equal("OpenAiCodex", catalog.RuntimeModel!.Provider);
            Assert.Equal("gpt-test", catalog.RuntimeModel.Model);
            Assert.Equal(HttpStatusCode.OK, systemGet.StatusCode);
            Assert.True(systemGet.Headers.CacheControl?.NoStore == true);
            Assert.Equal(LoopTrigger.HumanMessage, systemDefinition.Trigger);
            Assert.Equal(LoopMemoryScope.WorkspaceStartupContext, systemDefinition.MemoryScope);
            Assert.Equal(LoopEditMode.SystemLocked, systemDefinition.EditMode);
            Assert.Equal(canonicalSystemDefinition.Graph.EntryNodeId, systemDefinition.Graph.EntryNodeId);
            Assert.Equal(canonicalSystemDefinition.Graph.TerminalNodeIds, systemDefinition.Graph.TerminalNodeIds);
            Assert.Equal(canonicalSystemDefinition.Graph.Nodes.Length, systemDefinition.Graph.Nodes.Count);
            for (var index = 0; index < canonicalSystemDefinition.Graph.Nodes.Length; index++)
            {
                var expected = canonicalSystemDefinition.Graph.Nodes[index];
                var actual = systemDefinition.Graph.Nodes[index];
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.DisplayName, actual.DisplayName);
                Assert.Equal(expected.Description, actual.Description);
                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.EditMode, actual.EditMode);
                Assert.Equal(expected.CapabilityIds, actual.CapabilityIds);
            }

            Assert.Equal(canonicalSystemDefinition.Graph.Edges.Length, systemDefinition.Graph.Edges.Count);
            for (var index = 0; index < canonicalSystemDefinition.Graph.Edges.Length; index++)
            {
                var expected = canonicalSystemDefinition.Graph.Edges[index];
                var actual = systemDefinition.Graph.Edges[index];
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.FromNodeId, actual.FromNodeId);
                Assert.Equal(expected.ToNodeId, actual.ToNodeId);
                Assert.Equal(expected.Condition, actual.Condition);
                Assert.Equal(expected.Description, actual.Description);
            }
            Assert.All(systemDefinition.Graph.Nodes, node => Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, node.ExecutionSemantics));
            Assert.All(systemDefinition.Graph.Edges, edge => Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, edge.ExecutionSemantics));
            Assert.Equal(SystemLoopExecutionSemantics.AuthorityTopologyOnly, systemDefinition.ExecutionContract.GraphSemantics);
            Assert.Contains("does not certify", systemDefinition.ExecutionContract.Detail, StringComparison.Ordinal);
            Assert.False(systemDefinition.ExecutionContract.UsesGenericGraphDispatcher);
            Assert.DoesNotContain("\"inferenceSteps\"", systemJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"triggerPolicy\"", systemJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"exitPolicy\"", systemJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"toolAssignments\"", systemJson, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, malformedGet.StatusCode);
            Assert.Contains("invalid_loop_id", await malformedGet.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, systemUpdate.StatusCode);
            Assert.Contains("system_loop_locked", await systemUpdate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, systemDelete.StatusCode);
            Assert.Contains("system_loop_locked", await systemDelete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Loop_create_rejects_missing_or_null_first_save_definitions_as_invalid_requests()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { })).StatusCode);

            var missingDefinition = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "missing-definition" });
            var nullDefinition = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "null-definition", definition = (LoopDefinitionInput?)null });
            var missingBody = (await missingDefinition.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions))!;
            var nullBody = (await nullDefinition.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions))!;
            var catalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var catalog = (await catalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(_jsonOptions))!;

            Assert.Equal(HttpStatusCode.BadRequest, missingDefinition.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, nullDefinition.StatusCode);
            foreach (var response in new[] { missingBody, nullBody })
            {
                Assert.Equal("Invalid", response.Status);
                Assert.False(response.IsCommitted);
                var error = Assert.Single(response.ValidationErrors);
                Assert.Equal("definition_required", error.Code);
                Assert.Equal("definition", error.Field);
            }

            Assert.Empty(catalog.CustomDefinitions);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Loop_api_projects_crud_conflicts_and_hostile_text_as_json_data()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { })).StatusCode);
            var initialCatalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var initialCatalog = (await initialCatalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(_jsonOptions))!;
            var firstSaveDefinition = initialCatalog.DraftTemplate.Definition with { DisplayName = "First saved loop", Description = "Created only at explicit Save." };
            var firstSaveBody = new { operationId = "create-api-loop", definition = firstSaveDefinition };

            var unknownMember = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "unknown-field", definition = firstSaveDefinition, unexpected = true });
            var createResponse = await SendAsync(client, HttpMethod.Post, "/api/loops", token, firstSaveBody);
            var created = await createResponse.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions);
            var createdDefinition = Assert.IsType<LoopDefinitionSnapshot>(created!.Definition);
            var replayedCreate = await SendAsync(client, HttpMethod.Post, "/api/loops", token, firstSaveBody);
            var conflictingCreate = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "create-api-loop", definition = firstSaveDefinition with { Description = "Different first save." } });
            var hostileText = "</textarea><script>globalThis.pwned=true</script><!-- & «quoted»";
            var updateBody = CreateUpdateBody(createdDefinition, "update-api-loop", hostileText);
            var invalid = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "invalid-api-loop", " "));
            var writeTool = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "write-tool", "Write", toolAssignments: ["write"]));
            var numericEnumBody = CreateUpdateBody(createdDefinition, "numeric-enum", hostileText, promptSource: 1);
            var numericEnum = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, numericEnumBody);
            var updateResponse = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, updateBody);
            var updateJson = await updateResponse.Content.ReadAsStringAsync();
            using var updateDocument = JsonDocument.Parse(updateJson);
            var updated = JsonSerializer.Deserialize<LoopAuthoringResponse>(updateJson, _jsonOptions);
            var updatedDefinition = Assert.IsType<LoopDefinitionSnapshot>(updated!.Definition);
            var fetchedResponse = await SendAsync(client, HttpMethod.Get, $"/api/loops/{createdDefinition.Id}", token);
            var fetched = await fetchedResponse.Content.ReadFromJsonAsync<LoopDefinitionSnapshot>(_jsonOptions);
            var conflict = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "conflict-api-loop", "Conflicting edit"));
            var conflictBody = await conflict.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions);
            var populatedCatalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var populatedCatalog = await populatedCatalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(_jsonOptions);
            var deleteResponse = await SendAsync(client, HttpMethod.Delete, $"/api/loops/{createdDefinition.Id}", token, new { expectedDefinitionVersion = updatedDefinition.DefinitionVersion, operationId = "delete-api-loop" });
            var deleted = await deleteResponse.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions);
            var missing = await SendAsync(client, HttpMethod.Get, $"/api/loops/{createdDefinition.Id}", token);
            var missingUpdate = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "update-deleted-loop", "Deleted"));
            var finalCatalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var finalCatalog = await finalCatalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(_jsonOptions);

            Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.Equal("Created", created.Status);
            Assert.Equal("First saved loop", createdDefinition.DisplayName);
            Assert.Equal("Created only at explicit Save.", createdDefinition.Description);
            Assert.Equal(1, createdDefinition.DefinitionVersion);
            Assert.NotNull(Assert.Single(createdDefinition.InferenceSteps).Id);
            Assert.Equal("create-api-loop", createdDefinition.LastMutationOperationId);
            Assert.Equal(HttpStatusCode.OK, replayedCreate.StatusCode);
            Assert.Equal("Replayed", (await replayedCreate.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions))!.Status);
            Assert.Equal(HttpStatusCode.Conflict, conflictingCreate.StatusCode);
            Assert.Equal("Conflict", (await conflictingCreate.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions))!.Status);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("Invalid", (await invalid.Content.ReadFromJsonAsync<LoopAuthoringResponse>(_jsonOptions))!.Status);
            Assert.Equal(HttpStatusCode.BadRequest, writeTool.StatusCode);
            Assert.Contains("unsupported_tool_assignment", await writeTool.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, numericEnum.StatusCode);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Equal("application/json", updateResponse.Content.Headers.ContentType!.MediaType);
            Assert.Equal(hostileText, updateDocument.RootElement.GetProperty("definition").GetProperty("displayName").GetString());
            Assert.Equal("Updated", updated.Status);
            Assert.Equal(hostileText, updatedDefinition.DisplayName);
            Assert.Equal(hostileText, updatedDefinition.Description);
            Assert.Equal(hostileText, updatedDefinition.TriggerPolicy.PresetPrompt);
            Assert.Equal(hostileText, updatedDefinition.InferenceSteps.Single().Instruction);
            Assert.Equal(LoopTriggerPromptSource.Preset, updatedDefinition.TriggerPolicy.PromptSource);
            Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], updatedDefinition.ToolAssignments);
            Assert.Equal(HttpStatusCode.OK, fetchedResponse.StatusCode);
            Assert.Equal(updatedDefinition.ContentHash, fetched!.ContentHash);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("Conflict", conflictBody!.Status);
            Assert.Equal(updatedDefinition.DefinitionVersion, conflictBody.Conflict!.ActualDefinitionVersion);
            Assert.Equal(updatedDefinition.ContentHash, Assert.Single(populatedCatalog!.CustomDefinitions).ContentHash);
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
            Assert.Equal("Deleted", deleted!.Status);
            Assert.True(deleted.IsCommitted);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
            Assert.Empty(finalCatalog!.CustomDefinitions);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static object CreateUpdateBody(LoopDefinitionSnapshot? definition, string operationId, string text, object? promptSource = null, string[]? toolAssignments = null)
    {
        var contextPolicy = new
        {
            contextIn = new
            {
                includeRoleContext = true,
                includeTriggerPrompt = true,
                includeInvokingConversation = false,
                includeEarlierRetainedOutputs = true,
                includePreviousIterationResult = true
            },
            contextOut = new { retainForLoopReasoning = true, publishToInvokingConversation = false }
        };
        return new
        {
            expectedDefinitionVersion = definition?.DefinitionVersion ?? 1,
            operationId,
            definition = new
            {
                displayName = text,
                description = text,
                triggerPolicy = new { promptSource = promptSource ?? "preset", presetPrompt = text, includeInvokingConversation = false },
                inferenceSteps = new[]
                {
                    new
                    {
                        id = definition?.InferenceSteps.Single().Id ?? "system-placeholder-step",
                        name = "Inspect",
                        instruction = text,
                        contextPolicy = new { mode = "custom", customPolicy = contextPolicy }
                    }
                },
                toolAssignments = toolAssignments ?? ["list", "read", "search"],
                exitPolicy = new
                {
                    maxAdditionalIterations = 2,
                    decisionInstruction = text,
                    contextPolicy = new { mode = "custom", customPolicy = contextPolicy }
                }
            }
        };
    }

    private static LoopDefinitionInput CreateFirstSaveDefinition()
    {
        var inherited = new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null);
        return new LoopDefinitionInput(
            "Untitled loop",
            string.Empty,
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(null, "First step", "Complete the requested work.", inherited)],
            [],
            new LoopExitPolicy(0, "Complete when the requested work is done.", inherited));
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

    private static WebApplication CreateApp(string rootPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test" };
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
