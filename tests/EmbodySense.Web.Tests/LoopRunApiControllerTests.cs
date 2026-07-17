using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

public sealed class LoopRunApiControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Run_evidence_api_enforces_auth_initialization_bounds_and_safe_read_failures_without_starting_runtime()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, codexPath: null, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var unauthorized = await client.GetAsync("/api/loop-runs");
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var beforeInitialization = await SendAsync(client, "/api/loop-runs", token);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/api/workspace/init", token, HttpMethod.Post)).StatusCode);
            var paths = new WorkspacePaths(workspace.RootPath);
            const string transcriptEvidence = "existing conversation evidence must survive a run read";
            await File.WriteAllTextAsync(paths.CurrentConversationPath, transcriptEvidence);

            var list = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var summaries = await list.Content.ReadFromJsonAsync<LoopRunSummarySnapshot[]>(JsonOptions);
            var invalidMaximum = await SendAsync(client, "/api/loop-runs?maximumCount=0", token);
            var missing = await SendAsync(client, "/api/loop-runs/run-missing", token);
            var invalidId = await SendAsync(client, "/api/loop-runs/INVALID%20ID", token);
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopRunsPath, "loop-corrupt"));
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, "loop-corrupt", "run-corrupt.json"), "secret-provider-corruption");
            var corrupt = await SendAsync(client, "/api/loop-runs/run-corrupt", token);
            var corruptBody = await corrupt.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Empty(summaries!);
            Assert.Equal(transcriptEvidence, await File.ReadAllTextAsync(paths.CurrentConversationPath));
            Assert.Empty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidMaximum.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
            Assert.Contains("run_evidence_unavailable", corruptBody, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-provider-corruption", corruptBody, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Host_invocation_requires_server_owned_approval_owner_and_run_api_returns_its_durable_artifact()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            var host = app.Services.GetRequiredService<WebAgentRuntimeHost>();
            await host.InitializeWorkspaceAsync();
            var definition = await CreateInvocationLoopAsync(workspace);
            var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-web-run-api", "web custom task");

            await Assert.ThrowsAsync<ArgumentException>(() => host.InvokeLoopAsync(input, " "));
            var invocation = await host.InvokeLoopAsync(input, "connection-owned-by-hub");
            var list = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var summaries = await list.Content.ReadFromJsonAsync<LoopRunSummarySnapshot[]>(JsonOptions);
            var detailResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run!.Id}", token);
            var detail = await detailResponse.Content.ReadFromJsonAsync<LoopRunSnapshot>(JsonOptions);
            var quotaResponse = await SendAsync(client, "/api/loop-runs/quota", token);
            var quota = await quotaResponse.Content.ReadFromJsonAsync<LoopTraceQuotaSnapshot>(JsonOptions);
            var traceResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace", token);
            var trace = await traceResponse.Content.ReadFromJsonAsync<LoopTraceInspectionSnapshot>(JsonOptions);
            var unauthorizedDeletion = await client.PostAsJsonAsync($"/api/loop-runs/{invocation.Run.Id}/trace/delete", new { expectedTraceHash = trace!.PersistedArtifactHash, operationId = "delete-web-trace-unauthorized" }, JsonOptions);
            var hashMismatch = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = new string('0', 64), operationId = "delete-web-trace-mismatch" });
            var forgedIdentity = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace-forged", actor = "browser-forged" });
            var deletionResponse = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace" });
            var deletion = await deletionResponse.Content.ReadFromJsonAsync<LoopTraceDeletionResponse>(JsonOptions);
            var replayResponse = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace" });
            var replay = await replayResponse.Content.ReadFromJsonAsync<LoopTraceDeletionResponse>(JsonOptions);
            var tombstoneResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace", token);
            var tombstone = await tombstoneResponse.Content.ReadFromJsonAsync<LoopTraceInspectionSnapshot>(JsonOptions);
            var summariesAfterDeletion = await (await SendAsync(client, "/api/loop-runs?maximumCount=50", token)).Content.ReadFromJsonAsync<LoopRunSummarySnapshot[]>(JsonOptions);
            var quotaAfterDeletion = await (await SendAsync(client, "/api/loop-runs/quota", token)).Content.ReadFromJsonAsync<LoopTraceQuotaSnapshot>(JsonOptions);

            Assert.Equal("Admitted", invocation.AdmissionStatus);
            Assert.Equal("Completed", invocation.ExecutionStatus);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal(invocation.Run.Id, Assert.Single(summaries!).Id);
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.Equal(invocation.Run.Id, detail!.Id);
            Assert.Equal(invocation.Run.Context.ManifestHash, detail.Context.ManifestHash);
            Assert.Equal(HttpStatusCode.OK, quotaResponse.StatusCode);
            Assert.Equal(1, quota!.LiveTraceCount);
            Assert.Equal(1, quota.ActiveReservationCount);
            Assert.Equal(CustomLoopLimits.MaxTraceControlEventUtf8Bytes, quota.ReservedCapacityUtf8Bytes);
            Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
            Assert.False(trace!.IsDeleted);
            Assert.True(trace.PersistedArtifactUtf8Bytes > 0);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedDeletion.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, hashMismatch.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, forgedIdentity.StatusCode);
            Assert.Equal(HttpStatusCode.OK, deletionResponse.StatusCode);
            Assert.Equal("Deleted", deletion!.Status);
            Assert.True(deletion.IsCommitted);
            Assert.Equal("web", deletion.Tombstone!.DeletionSurface);
            Assert.NotEqual("browser-forged", deletion.Tombstone.DeletionActor);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            Assert.Equal("Replayed", replay!.Status);
            Assert.Equal(HttpStatusCode.OK, tombstoneResponse.StatusCode);
            Assert.True(tombstone!.IsDeleted);
            Assert.True(Assert.Single(summariesAfterDeletion!).IsDeleted);
            Assert.Equal(trace.PersistedArtifactHash, tombstone.OriginalTraceHash);
            Assert.Equal(0, quotaAfterDeletion!.LiveTraceCount);
            Assert.Equal(1, quotaAfterDeletion.TombstoneCount);
            Assert.IsAssignableFrom<IWebLoopRuntimeInvoker>(host);
            Assert.Same(host, app.Services.GetRequiredService<IWebLoopRuntimeInvoker>());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Pause_and_cancel_routes_require_auth_and_accept_only_the_frontend_control_body()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, codexPath: null, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions))!.Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/api/workspace/init", token, HttpMethod.Post)).StatusCode);

            var unauthorized = await client.PostAsJsonAsync("/api/loop-runs/run-missing/pause", new { expectedLifecycleVersion = 1, operationId = "pause-unauthorized" }, JsonOptions);
            var pause = await SendControlAsync(client, "/api/loop-runs/run-missing/pause", token, new { expectedLifecycleVersion = 1, operationId = "pause-missing" });
            var cancel = await SendControlAsync(client, "/api/loop-runs/run-missing/cancel", token, new { expectedLifecycleVersion = 1, operationId = "cancel-missing" });
            var invalid = await SendControlAsync(client, "/api/loop-runs/run-missing/pause", token, new { expectedLifecycleVersion = 0, operationId = "pause-invalid" });
            var unknownField = await SendControlAsync(client, "/api/loop-runs/run-missing/cancel", token, new { expectedLifecycleVersion = 1, operationId = "cancel-unknown", ownerConnectionId = "forged-owner" });

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, pause.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-web-run-api")).Definition);
        var input = new LoopDefinitionInput(
            "Web runtime loop",
            "One inference step for Web projection verification.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Respond", "Respond to the admitted trigger prompt.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-web-run-api", input);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { }, options: JsonOptions);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendControlAsync(HttpClient client, string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        return await client.SendAsync(request);
    }

    private static WebApplication CreateApp(string rootPath, string? codexPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = codexPath is null
            ? new[] { "--workdir", rootPath, "--port", port.ToString() }
            : new[] { "--workdir", rootPath, "--port", port.ToString(), "--codex-path", codexPath, "--model", "test-model" };
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

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-loop-run-api-codex.ps1");
        var commandPath = workspace.File("fake-loop-run-api-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            $threadId = "thread-test"

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json
                switch ($message.method) {
                    "initialize" { Write-ProtocolJson @{ id = $message.id; result = @{} } }
                    "initialized" { }
                    "thread/start" { Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } } }
                    "turn/start" {
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $currentUserMarker = "Current user message:"
                        $currentUserIndex = $userText.IndexOf($currentUserMarker)
                        if ($currentUserIndex -ge 0) { $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim() }
                        $text = "web loop response: $userText"
                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = $turnId; delta = $text } }
                        Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-loop-run-api-codex.ps1" %*
            """);
        return commandPath;
    }
}
