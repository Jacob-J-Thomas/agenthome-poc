using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;

namespace EmbodySense.Web.Tests;

public sealed class LoopApiControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

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
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var uninitializedCatalog = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var uninitializedCreate = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "create-before-init" });
            var initialized = await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { });
            var catalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var catalog = await catalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(JsonOptions);
            var systemGet = await SendAsync(client, HttpMethod.Get, "/api/loops/default-conversation", token);
            var malformedGet = await SendAsync(client, HttpMethod.Get, "/api/loops/INVALID%20ID", token);
            var systemUpdate = await SendAsync(client, HttpMethod.Put, "/api/loops/default-conversation", token, CreateUpdateBody(catalog!.SystemDefault, "system-update", "System loop"));
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
            Assert.Equal(
                [LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search, LoopToolAssignment.Append, LoopToolAssignment.Write, LoopToolAssignment.Delete],
                catalog.SystemDefault.ToolAssignments);
            Assert.Equal([LoopToolAssignment.List, LoopToolAssignment.Read, LoopToolAssignment.Search], catalog.Tools.CustomAssignable);
            Assert.Equal(LoopCustomToolAuthorityCeiling.WorkspaceReadOnly, catalog.Tools.CustomAuthorityCeiling);
            Assert.Equal("OpenAiCodex", catalog.RuntimeModel!.Provider);
            Assert.Null(catalog.RuntimeModel.Model);
            Assert.Equal(HttpStatusCode.OK, systemGet.StatusCode);
            Assert.True(systemGet.Headers.CacheControl?.NoStore == true);
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
    public async Task Loop_api_projects_crud_conflicts_and_hostile_text_as_json_data()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/workspace/init", token, new { })).StatusCode);

            var unknownMember = await SendRawJsonAsync(client, HttpMethod.Post, "/api/loops", token, """{"operationId":"unknown-field","unexpected":true}""");
            var createResponse = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "create-api-loop" });
            var created = await createResponse.Content.ReadFromJsonAsync<LoopAuthoringResponse>(JsonOptions);
            var createdDefinition = Assert.IsType<LoopDefinitionSnapshot>(created!.Definition);
            var replayedCreate = await SendAsync(client, HttpMethod.Post, "/api/loops", token, new { operationId = "create-api-loop" });
            var hostileText = "</textarea><script>globalThis.pwned=true</script><!-- & «quoted»";
            var updateBody = CreateUpdateBody(createdDefinition, "update-api-loop", hostileText);
            var invalid = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "invalid-api-loop", " "));
            var writeTool = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "write-tool", "Write", toolAssignments: ["write"]));
            var numericEnumBody = CreateUpdateBody(createdDefinition, "numeric-enum", hostileText, promptSource: 1);
            var numericEnum = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, numericEnumBody);
            var updateResponse = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, updateBody);
            var updateJson = await updateResponse.Content.ReadAsStringAsync();
            using var updateDocument = JsonDocument.Parse(updateJson);
            var updated = JsonSerializer.Deserialize<LoopAuthoringResponse>(updateJson, JsonOptions);
            var updatedDefinition = Assert.IsType<LoopDefinitionSnapshot>(updated!.Definition);
            var fetchedResponse = await SendAsync(client, HttpMethod.Get, $"/api/loops/{createdDefinition.Id}", token);
            var fetched = await fetchedResponse.Content.ReadFromJsonAsync<LoopDefinitionSnapshot>(JsonOptions);
            var conflict = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "conflict-api-loop", "Conflicting edit"));
            var conflictBody = await conflict.Content.ReadFromJsonAsync<LoopAuthoringResponse>(JsonOptions);
            var populatedCatalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var populatedCatalog = await populatedCatalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(JsonOptions);
            var deleteResponse = await SendAsync(client, HttpMethod.Delete, $"/api/loops/{createdDefinition.Id}", token, new { expectedDefinitionVersion = updatedDefinition.DefinitionVersion, operationId = "delete-api-loop" });
            var deleted = await deleteResponse.Content.ReadFromJsonAsync<LoopAuthoringResponse>(JsonOptions);
            var missing = await SendAsync(client, HttpMethod.Get, $"/api/loops/{createdDefinition.Id}", token);
            var missingUpdate = await SendAsync(client, HttpMethod.Put, $"/api/loops/{createdDefinition.Id}", token, CreateUpdateBody(createdDefinition, "update-deleted-loop", "Deleted"));
            var finalCatalogResponse = await SendAsync(client, HttpMethod.Get, "/api/loops", token);
            var finalCatalog = await finalCatalogResponse.Content.ReadFromJsonAsync<LoopAuthoringCatalog>(JsonOptions);

            Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.Equal("Created", created.Status);
            Assert.Equal(HttpStatusCode.OK, replayedCreate.StatusCode);
            Assert.Equal("Replayed", (await replayedCreate.Content.ReadFromJsonAsync<LoopAuthoringResponse>(JsonOptions))!.Status);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("Invalid", (await invalid.Content.ReadFromJsonAsync<LoopAuthoringResponse>(JsonOptions))!.Status);
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

    private static object CreateUpdateBody(LoopDefinitionSnapshot definition, string operationId, string text, object? promptSource = null, string[]? toolAssignments = null)
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
            expectedDefinitionVersion = definition.DefinitionVersion,
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
                        id = definition.InferenceSteps.Single().Id,
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
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

    private static WebApplication CreateApp(string rootPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        options = WebRunOptions.FromArguments(["--workdir", rootPath, "--port", port.ToString()]);
        var builder = Program.CreateBuilder(["--workdir", rootPath, "--port", port.ToString()], options);
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
