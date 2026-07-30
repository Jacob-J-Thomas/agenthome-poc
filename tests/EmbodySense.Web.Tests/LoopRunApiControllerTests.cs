using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Web;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Models;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Controllers;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

public sealed class LoopRunApiControllerTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-20T12:00:00+00:00");

    [Fact]
    public void Monitor_etag_changes_for_every_previously_omitted_summary_field()
    {
        var summary = new LoopRunSummarySnapshot("run-test", "loop-test", "invoke-test", 1, 2, "Running", _timestamp, _timestamp.AddSeconds(1), null, 1, 2, null, false);
        string Etag(LoopRunSummarySnapshot value, string artifactHash = "a") => LoopRunMonitorEtag.Create(value, artifactHash);

        Assert.NotEqual(Etag(summary), Etag(summary with { DefinitionVersion = 2 }));
        Assert.NotEqual(Etag(summary), Etag(summary with { CreatedAtUtc = summary.CreatedAtUtc.AddTicks(1) }));
        Assert.NotEqual(Etag(summary), Etag(summary, "b"));
        Assert.Throws<ArgumentNullException>(() => LoopRunMonitorEtag.Create(null!, "a"));
        Assert.Throws<ArgumentException>(() => LoopRunMonitorEtag.Create(summary, ""));
    }

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
            var unauthorizedControlReceipt = await client.GetAsync("/api/loop-runs/controls/control-web");
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;
            var beforeInitialization = await SendAsync(client, "/api/loop-runs", token);
            var controlBeforeInitialization = await SendAsync(client, "/api/loop-runs/controls/control-web", token);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/api/workspace/init", token, HttpMethod.Post)).StatusCode);
            var paths = new WorkspacePaths(workspace.RootPath);
            const string TranscriptEvidence = "existing conversation evidence must survive a run read";
            await File.WriteAllTextAsync(paths.CurrentConversationPath, TranscriptEvidence);

            var list = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var summaries = await list.Content.ReadFromJsonAsync<LoopRunSummaryPageSnapshot>(_jsonOptions);
            var invalidMaximum = await SendAsync(client, "/api/loop-runs?maximumCount=0", token);
            var invalidCursor = await SendAsync(client, "/api/loop-runs?cursor=not-a-cursor", token);
            var invalidLoopFilter = await SendAsync(client, "/api/loop-runs?loopId=INVALID%20ID", token);
            var missing = await SendAsync(client, "/api/loop-runs/run-missing", token);
            var invalidId = await SendAsync(client, "/api/loop-runs/INVALID%20ID", token);
            var monitoredRun = await CreateInterruptedRunAsync(new CustomLoopRunStore(paths));
            await CreateCompletedInvocationReceiptAsync(paths, monitoredRun);
            var invocationReceipt = await SendAsync(client, $"/api/loop-runs/invocations/{monitoredRun.AdmissionOperationId}", token);
            var invocationSnapshot = await invocationReceipt.Content.ReadFromJsonAsync<LoopInvocationOperationSnapshot>(_jsonOptions);
            var control = await BeginControlReceiptAsync(paths);
            using var controlLease = control.Lease;
            var pendingControlReceipt = await SendAsync(client, $"/api/loop-runs/controls/{control.Operation.OperationId}", token);
            var pendingControlSnapshot = await pendingControlReceipt.Content.ReadFromJsonAsync<LoopControlOperationSnapshot>(_jsonOptions);
            Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await control.Store.CompleteAsync(control.Operation with
            {
                UpdatedAtUtc = control.Operation.UpdatedAtUtc.AddSeconds(1),
                State = CustomLoopControlOperationState.Complete,
                Outcome = CustomLoopControlStatus.Paused,
                ResultLifecycleVersion = 3,
                ResultRunStatus = CustomLoopRunStatus.Paused,
                OutcomeAuditRecorded = true,
                Detail = "The run paused."
            })).Status);
            var completedControlReceipt = await SendAsync(client, $"/api/loop-runs/controls/{control.Operation.OperationId}", token);
            var completedControlJson = await completedControlReceipt.Content.ReadAsStringAsync();
            var completedControlSnapshot = JsonSerializer.Deserialize<LoopControlOperationSnapshot>(completedControlJson, _jsonOptions);
            var missingControlReceipt = await SendAsync(client, "/api/loop-runs/controls/control-missing", token);
            var invalidControlReceipt = await SendAsync(client, "/api/loop-runs/controls/INVALID%20ID", token);
            var monitor = await SendAsync(client, $"/api/loop-runs/{monitoredRun.Id}/monitor", token);
            var monitorSummary = await monitor.Content.ReadFromJsonAsync<LoopRunSummarySnapshot>(_jsonOptions);
            var monitorEtag = monitor.Headers.ETag;
            using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/loop-runs/{monitoredRun.Id}/monitor");
            conditionalRequest.Headers.Add(WebSessionSecurity.HeaderName, token);
            conditionalRequest.Headers.IfNoneMatch.Add(monitorEtag!);
            var unchangedMonitor = await client.SendAsync(conditionalRequest);
            using var weakListRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/loop-runs/{monitoredRun.Id}/monitor");
            weakListRequest.Headers.Add(WebSessionSecurity.HeaderName, token);
            weakListRequest.Headers.TryAddWithoutValidation("If-None-Match", $"\"older\", W/{monitorEtag}");
            var weakListMonitor = await client.SendAsync(weakListRequest);
            using var wildcardRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/loop-runs/{monitoredRun.Id}/monitor");
            wildcardRequest.Headers.Add(WebSessionSecurity.HeaderName, token);
            wildcardRequest.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var wildcardMonitor = await client.SendAsync(wildcardRequest);
            var canonicalRun = (await new CustomLoopRunStore(paths).GetAsync(monitoredRun.Id))!;
            var changedRun = canonicalRun with { Events = [.. canonicalRun.Events[..^1], canonicalRun.Events[^1] with { Detail = "Externally replaced canonical event evidence." }] };
            var artifactPath = Path.Combine(paths.CustomLoopRunsPath, changedRun.LoopId, changedRun.Id + ".json");
            await File.WriteAllBytesAsync(artifactPath, CustomLoopRunArtifactSerializer.Serialize(changedRun));
            HttpResponseMessage? changedMonitor = null;
            for (var attempt = 0; attempt < 20 && changedMonitor?.StatusCode != HttpStatusCode.OK; attempt++)
            {
                changedMonitor?.Dispose();
                using var changedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/loop-runs/{monitoredRun.Id}/monitor");
                changedRequest.Headers.Add(WebSessionSecurity.HeaderName, token);
                changedRequest.Headers.IfNoneMatch.Add(monitorEtag!);
                changedMonitor = await client.SendAsync(changedRequest);
                if (changedMonitor.StatusCode != HttpStatusCode.OK) await Task.Delay(25);
            }
            Directory.CreateDirectory(Path.Combine(paths.CustomLoopRunsPath, "loop-corrupt"));
            await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, "loop-corrupt", "run-corrupt.json"), "secret-provider-corruption");
            var corrupt = await SendAsync(client, "/api/loop-runs/run-corrupt", token);
            var corruptBody = await corrupt.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedControlReceipt.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, beforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, controlBeforeInitialization.StatusCode);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Empty(summaries!.Items);
            Assert.Null(summaries.ContinuationCursor);
            Assert.Equal(TranscriptEvidence, await File.ReadAllTextAsync(paths.CurrentConversationPath));
            Assert.Empty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidMaximum.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidLoopFilter.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);
            Assert.Equal(HttpStatusCode.OK, invocationReceipt.StatusCode);
            Assert.Equal("Complete", invocationSnapshot?.State);
            Assert.Equal("Admitted", invocationSnapshot?.Outcome);
            Assert.Equal(monitoredRun.Id, invocationSnapshot?.RunId);
            Assert.Equal(HttpStatusCode.OK, pendingControlReceipt.StatusCode);
            Assert.Equal("Pending", pendingControlSnapshot?.State);
            Assert.Equal("Unknown", pendingControlSnapshot?.Outcome);
            Assert.False(pendingControlSnapshot?.CompletionDurablyProved);
            Assert.Equal(HttpStatusCode.OK, completedControlReceipt.StatusCode);
            Assert.Equal("control-web", completedControlSnapshot?.OperationId);
            Assert.Equal("Pause", completedControlSnapshot?.Kind);
            Assert.Equal("run-web-recovery", completedControlSnapshot?.RunId);
            Assert.Equal(2, completedControlSnapshot?.ExpectedLifecycleVersion);
            Assert.Equal("Complete", completedControlSnapshot?.State);
            Assert.Equal("Paused", completedControlSnapshot?.Outcome);
            Assert.Equal(3, completedControlSnapshot?.ResultLifecycleVersion);
            Assert.Equal("Paused", completedControlSnapshot?.ResultRunStatus);
            Assert.True(completedControlSnapshot?.OutcomeAuditRecorded);
            Assert.True(completedControlSnapshot?.CompletionDurablyProved);
            Assert.DoesNotContain("\"detail\"", completedControlJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"actor\"", completedControlJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.NotFound, missingControlReceipt.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidControlReceipt.StatusCode);
            Assert.Equal(HttpStatusCode.OK, monitor.StatusCode);
            Assert.Equal(monitoredRun.Id, monitorSummary?.Id);
            Assert.Equal(monitoredRun.LifecycleVersion, monitorSummary?.LifecycleVersion);
            Assert.NotNull(monitorEtag);
            Assert.Equal(HttpStatusCode.NotModified, unchangedMonitor.StatusCode);
            Assert.Empty(await unchangedMonitor.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotModified, weakListMonitor.StatusCode);
            Assert.Empty(await weakListMonitor.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotModified, wildcardMonitor.StatusCode);
            Assert.Empty(await wildcardMonitor.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, changedMonitor?.StatusCode);
            Assert.NotEqual(monitorEtag, changedMonitor?.Headers.ETag);
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
    public async Task Run_evidence_api_surfaces_unsupported_discovery_index_cleanup_without_rewriting_the_index()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, codexPath: null, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/api/workspace/init", token, HttpMethod.Post)).StatusCode);
            var paths = new WorkspacePaths(workspace.RootPath);
            Directory.CreateDirectory(paths.CustomLoopRunsPath);
            var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
            const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
            await File.WriteAllTextAsync(indexPath, UnsupportedIndex);

            var response = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("unsupported_loop_persistence_schema", body, StringComparison.Ordinal);
            Assert.Contains("Delete `.custom-loop-run-index.json`", body, StringComparison.Ordinal);
            Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));
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
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;
            var host = app.Services.GetRequiredService<WebAgentRuntimeHost>();
            await host.InitializeWorkspaceAsync();
            var definition = await CreateInvocationLoopAsync(workspace);
            var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-web-run-api", "web custom task");

            await Assert.ThrowsAsync<ArgumentException>(() => host.InvokeLoopAsync(input, " "));
            var invocation = await host.InvokeLoopAsync(input, "connection-owned-by-hub");
            var list = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var summaries = await list.Content.ReadFromJsonAsync<LoopRunSummaryPageSnapshot>(_jsonOptions);
            var detailResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run!.Id}", token);
            var detail = await detailResponse.Content.ReadFromJsonAsync<LoopRunSnapshot>(_jsonOptions);
            var quotaResponse = await SendAsync(client, "/api/loop-runs/quota", token);
            var quota = await quotaResponse.Content.ReadFromJsonAsync<LoopTraceQuotaSnapshot>(_jsonOptions);
            var traceResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace", token);
            var trace = await traceResponse.Content.ReadFromJsonAsync<LoopTraceInspectionSnapshot>(_jsonOptions);
            var unauthorizedDeletion = await client.PostAsJsonAsync($"/api/loop-runs/{invocation.Run.Id}/trace/delete", new { expectedTraceHash = trace!.PersistedArtifactHash, operationId = "delete-web-trace-unauthorized" }, _jsonOptions);
            var hashMismatch = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = new string('0', 64), operationId = "delete-web-trace-mismatch" });
            var forgedIdentity = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace-forged", actor = "browser-forged" });
            var deletionResponse = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace" });
            var deletion = await deletionResponse.Content.ReadFromJsonAsync<LoopTraceDeletionResponse>(_jsonOptions);
            var replayResponse = await SendControlAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace/delete", token, new { expectedTraceHash = trace.PersistedArtifactHash, operationId = "delete-web-trace" });
            var replay = await replayResponse.Content.ReadFromJsonAsync<LoopTraceDeletionResponse>(_jsonOptions);
            var tombstoneResponse = await SendAsync(client, $"/api/loop-runs/{invocation.Run.Id}/trace", token);
            var tombstone = await tombstoneResponse.Content.ReadFromJsonAsync<LoopTraceInspectionSnapshot>(_jsonOptions);
            var summariesAfterDeletion = await (await SendAsync(client, $"/api/loop-runs?maximumCount=50&loopId={definition.Id}", token)).Content.ReadFromJsonAsync<LoopRunSummaryPageSnapshot>(_jsonOptions);
            var quotaAfterDeletion = await (await SendAsync(client, "/api/loop-runs/quota", token)).Content.ReadFromJsonAsync<LoopTraceQuotaSnapshot>(_jsonOptions);

            Assert.Equal("Admitted", invocation.AdmissionStatus);
            Assert.Equal("Completed", invocation.ExecutionStatus);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal(invocation.Run.Id, Assert.Single(summaries!.Items).Id);
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.True(detailResponse.Headers.CacheControl?.NoStore == true);
            Assert.Equal(invocation.Run.Id, detail!.Id);
            Assert.Equal(invocation.Run.Context.ManifestHash, detail.Context.ManifestHash);
            Assert.Equal(HttpStatusCode.OK, quotaResponse.StatusCode);
            Assert.Equal(1, quota!.LiveTraceCount);
            Assert.Equal(1, quota.ActiveReservationCount);
            Assert.Equal(CustomLoopLimits.MaxTraceControlEventUtf8Bytes, quota.ReservedCapacityUtf8Bytes);
            Assert.Equal(HttpStatusCode.OK, traceResponse.StatusCode);
            Assert.True(traceResponse.Headers.CacheControl?.NoStore == true);
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
            Assert.True(Assert.Single(summariesAfterDeletion!.Items).IsDeleted);
            Assert.Equal(trace.PersistedArtifactHash, tombstone.OriginalTraceHash);
            Assert.Equal(0, quotaAfterDeletion!.LiveTraceCount);
            Assert.Equal(1, quotaAfterDeletion.TombstoneCount);
            Assert.Equal(1, quotaAfterDeletion.DeletionOperationCount);
            Assert.Equal(CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace, quotaAfterDeletion.MaximumDeletionOperationCount);
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
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/api/workspace/init", token, HttpMethod.Post)).StatusCode);

            var unauthorized = await client.PostAsJsonAsync("/api/loop-runs/run-missing/pause", new { expectedLifecycleVersion = 1, operationId = "pause-unauthorized" }, _jsonOptions);
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

    [Fact]
    public async Task Pause_and_cancel_routes_surface_unsupported_discovery_index_cleanup()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CustomLoopRunStore(paths);
        var interrupted = await CreateInterruptedRunAsync(store);
        var runningAt = interrupted.UpdatedAtUtc.AddSeconds(1);
        var running = interrupted with
        {
            LifecycleVersion = interrupted.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = runningAt,
            ExecutionClock = new CustomLoopExecutionClock(0, runningAt),
            Events = [.. interrupted.Events, RunEvent(interrupted.Events.Length + 1L, "web-unsupported-index-running", CustomLoopRunEventKind.LifecycleChanged) with { TimestampUtc = runningAt }]
        };
        Assert.True(CustomLoopRunValidator.Validate(running).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, interrupted.LifecycleVersion)).Status);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;

            var pause = await SendControlAsync(client, $"/api/loop-runs/{running.Id}/pause", token, new { expectedLifecycleVersion = running.LifecycleVersion, operationId = "pause-unsupported-index" });
            var cancel = await SendControlAsync(client, $"/api/loop-runs/{running.Id}/cancel", token, new { expectedLifecycleVersion = running.LifecycleVersion, operationId = "cancel-unsupported-index" });
            var pauseBody = await pause.Content.ReadAsStringAsync();
            var cancelBody = await cancel.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, pause.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, cancel.StatusCode);
            Assert.Contains("unsupported_loop_persistence_schema", pauseBody, StringComparison.Ordinal);
            Assert.Contains("Delete `.custom-loop-run-index.json`", pauseBody, StringComparison.Ordinal);
            Assert.Contains("unsupported_loop_persistence_schema", cancelBody, StringComparison.Ordinal);
            Assert.Contains("Delete `.custom-loop-run-index.json`", cancelBody, StringComparison.Ordinal);
            Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Run_evidence_api_recovers_interrupted_runs_before_exposing_lifecycle_state()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        const string TranscriptEvidence = """
            {"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-07-20T11:58:00+00:00","role":"user","content":"recovered user prompt"}
            {"schemaVersion":1,"conversationId":"current","sequence":2,"timestampUtc":"2026-07-20T11:59:00+00:00","role":"assistant","content":"recovered assistant response"}
            """;
        await File.WriteAllTextAsync(paths.CurrentConversationPath, TranscriptEvidence);
        var conversationIdentity = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version;
        var interrupted = await CreateInterruptedRunAsync(new CustomLoopRunStore(paths), conversationIdentity);
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = (await client.GetFromJsonAsync<WebSessionInfo>("/api/session", _jsonOptions))!.Token;

            var response = await SendAsync(client, "/api/loop-runs?maximumCount=50", token);
            var recovered = Assert.Single((await response.Content.ReadFromJsonAsync<LoopRunSummaryPageSnapshot>(_jsonOptions))!.Items);
            var detail = await (await SendAsync(client, $"/api/loop-runs/{interrupted.Id}", token)).Content.ReadFromJsonAsync<LoopRunSnapshot>(_jsonOptions);
            var transcript = await app.Services.GetRequiredService<WebAgentRuntimeHost>().GetCurrentTranscriptAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(interrupted.Id, recovered.Id);
            Assert.Equal("Paused", recovered.Status);
            Assert.Equal(interrupted.LifecycleVersion + 1, detail!.LifecycleVersion);
            Assert.Collection(
                transcript!,
                message =>
                {
                    Assert.Equal("User", message.Role);
                    Assert.Equal("recovered user prompt", message.Content);
                },
                message =>
                {
                    Assert.Equal("Assistant", message.Role);
                    Assert.Equal("recovered assistant response", message.Content);
                });
            Assert.Equal(TranscriptEvidence, await File.ReadAllTextAsync(paths.CurrentConversationPath));
            Assert.Empty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
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

    private static async Task<CustomLoopRunRecord> CreateInterruptedRunAsync(CustomLoopRunStore store, string? invokingConversationIdentity = null)
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-web-recovery", "default-role", "step-1", "create-web-recovery", _timestamp);
        var admittedEvent = RunEvent(1, "web-recovery-admitted", CustomLoopRunEventKind.Admitted);
        var conversation = invokingConversationIdentity is null ? null : new CustomLoopConversationReference(invokingConversationIdentity, new string('c', CustomLoopLimits.Sha256HexCharacters), _timestamp);
        var admitted = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-web-recovery", definition.Id, 1, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-web-recovery", "web", string.Empty, definition, "Initial prompt", conversation, CustomLoopContextSnapshot.CreateEmpty(_timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admittedEvent], null, null, null);
        admitted = CustomLoopAdmissionRequestHash.Apply(admitted);
        Assert.True(CustomLoopRunValidator.Validate(admitted).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var audited = admitted with
        {
            LifecycleVersion = 2,
            Events = [admittedEvent, RunEvent(2, "web-recovery-admission-audit", CustomLoopRunEventKind.AdmissionAuditCompleted)]
        };
        Assert.True(CustomLoopRunValidator.Validate(audited).IsValid);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, admitted.LifecycleVersion)).Status);
        return audited;
    }

    private static CustomLoopRunEvent RunEvent(long sequence, string id, CustomLoopRunEventKind kind) => new(sequence, id, _timestamp, kind, null, null, null, kind.ToString(), [], null, null, null, null, null, null, null, null, null, null);

    private static async Task CreateCompletedInvocationReceiptAsync(WorkspacePaths paths, CustomLoopRunRecord run)
    {
        var requestHash = CustomLoopInvocationRequestHash.Compute(
            run.AdmissionOperationId,
            run.LoopId,
            run.AdmittedDefinition.DefinitionVersion,
            run.AdmittedDefinition.ContentHash,
            run.AdmissionActor,
            run.Surface,
            run.AdmittedDefinition.RoleId,
            run.TriggerPrompt,
            run.ModelSnapshot.Provider,
            run.ModelSnapshot.Model);
        var pending = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            run.AdmissionOperationId,
            requestHash,
            run.LoopId,
            run.AdmittedDefinition.DefinitionVersion,
            run.AdmittedDefinition.ContentHash,
            run.AdmissionActor,
            run.Surface,
            run.AdmittedDefinition.RoleId,
            CustomLoopInvocationRequestHash.ComputePromptHash(run.TriggerPrompt),
            run.ModelSnapshot.Provider,
            run.ModelSnapshot.Model,
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            run.CreatedAtUtc,
            run.CreatedAtUtc,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The invocation is pending.");
        var store = new CustomLoopInvocationOperationStore(paths);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        var bound = pending with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = new string('a', CustomLoopLimits.Sha256HexCharacters),
            ContextIdentityHash = new string('b', CustomLoopLimits.Sha256HexCharacters)
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(bound)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(bound with
        {
            UpdatedAtUtc = run.UpdatedAtUtc,
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Admitted,
            AdmissionStatus = "Admitted",
            RunId = run.Id,
            Detail = "The run was admitted."
        })).Status);
    }

    private static async Task<(CustomLoopControlOperationStore Store, CustomLoopControlOperation Operation, ICustomLoopControlOperationLease Lease)> BeginControlReceiptAsync(WorkspacePaths paths)
    {
        var pending = new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            "control-web",
            CustomLoopControlRequestHash.Compute(CustomLoopControlKind.Pause, "run-web-recovery", 2, "control-web", "web"),
            CustomLoopControlKind.Pause,
            "run-web-recovery",
            2,
            "web",
            _timestamp,
            _timestamp,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "The control operation is pending.");
        var store = new CustomLoopControlOperationStore(paths);
        var begun = await store.BeginAsync(pending);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, begun.Status);
        return (store, Assert.IsType<CustomLoopControlOperation>(begun.Operation), Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(begun.Lease));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { }, options: _jsonOptions);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendControlAsync(HttpClient client, string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: _jsonOptions) };
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
