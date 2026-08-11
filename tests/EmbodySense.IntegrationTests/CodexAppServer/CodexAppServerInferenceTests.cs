using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using System.Text.Json;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.CodexAppServer;

public sealed class CodexAppServerInferenceTests
{
    private static readonly JsonSerializerOptions _auditJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GenerateAsync_streams_agent_message_deltas_and_returns_completed_message()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"hello "}"""),
            Notification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"world"}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"hello world","phase":"final_answer"}]}}"""));
        var providerRequestStarted = false;
        var client = CreateClient(transport, providerRequestStarted: () => providerRequestStarted = true);
        var chunks = new List<string>();

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("say hello"), (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal(["hello ", "world"], chunks);
        Assert.Equal("hello world", response.OutputText);
        Assert.True(providerRequestStarted);
        Assert.Equal("turn-1", response.ProviderResponseId);
        Assert.Contains(transport.Writes, line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString() == "thread/start");
        Assert.Contains(transport.Writes, line => line.Contains("\"shell_tool\":false", StringComparison.Ordinal));
        Assert.Contains(transport.Writes, line => line.Contains("\"ephemeral\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_classifies_a_failed_completion_as_a_conclusive_terminal_provider_outcome()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"provider rejected the turn"},"items":[]}}"""));
        var client = CreateClient(transport);
        var dispatchStarted = false;

        var exception = await Assert.ThrowsAsync<LlmInferenceTerminalFailureException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("fail conclusively"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                dispatchStarted = true;
                await commitTransportWrite(token);
            }));

        Assert.True(dispatchStarted);
        Assert.Equal("turn-1", exception.ProviderResponseId);
        Assert.Contains("provider rejected the turn", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_classifies_a_turn_start_rejection_as_a_conclusive_terminal_provider_outcome()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            """{"id":3,"error":{"code":-32602,"message":"turn request rejected","data":{"turnId":"turn-rejected"}}}""");
        var client = CreateClient(transport);
        var dispatchStarted = false;

        var exception = await Assert.ThrowsAsync<LlmInferenceTerminalFailureException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("reject conclusively"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                dispatchStarted = true;
                await commitTransportWrite(token);
            }));

        Assert.True(dispatchStarted);
        Assert.Equal("turn-rejected", exception.ProviderResponseId);
        Assert.Contains("turn request rejected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_preserves_an_observed_success_when_completion_audit_fails()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"observed answer","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, workingDirectory: workspace.RootPath);
        FileStream? auditLock = null;

        try
        {
            var exception = await Assert.ThrowsAsync<LlmInferenceObservedResponseException>(() => client.GenerateAsync(
                LlmInferenceRequest.FromUserText("observe success"),
                responseChunkHandler: null,
                CancellationToken.None,
                async (commitTransportWrite, token) =>
                {
                    auditLock = new FileStream(paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    await commitTransportWrite(token);
                }));

            Assert.Equal("observed answer", exception.Response.OutputText);
            Assert.Equal("turn-1", exception.Response.ProviderResponseId);
            Assert.Contains("must not be redispatched", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (auditLock is not null)
            {
                await auditLock.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_preserves_a_conclusive_failure_when_completion_audit_also_fails()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"provider rejected the turn"},"items":[]}}"""));
        var client = CreateClient(transport, workingDirectory: workspace.RootPath);
        FileStream? auditLock = null;

        try
        {
            var exception = await Assert.ThrowsAsync<LlmInferenceTerminalFailureException>(() => client.GenerateAsync(
                LlmInferenceRequest.FromUserText("observe failure"),
                responseChunkHandler: null,
                CancellationToken.None,
                async (commitTransportWrite, token) =>
                {
                    auditLock = new FileStream(paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    await commitTransportWrite(token);
                }));

            Assert.Equal("turn-1", exception.ProviderResponseId);
            Assert.Contains("provider rejected the turn", exception.Message, StringComparison.Ordinal);
            Assert.Contains("completion audit could not be persisted", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (auditLock is not null)
            {
                await auditLock.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_routes_dynamic_tool_calls_through_tool_broker()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("shared", "note.txt"), "tool-visible note");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(99, "item/tool/call", """{"threadId":"thread-1","turnId":"turn-1","callId":"call-1","namespace":"embodysense","tool":"command","arguments":{"command":"read","path":"shared/note.txt"}}"""),
            Notification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"The note says tool-visible note."}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"The note says tool-visible note.","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, broker, workspace.RootPath);
        var correlation = new LlmInferenceCorrelation(
            "provider-attempt-1",
            "provider-correlation-1",
            new ToolAuditCorrelation("run-1", BuiltInLoopIds.DefaultConversation, "default-assistant", 1, new string('a', 64), 1, "provider-adapter", 1, "provider-correlation-1", "read,write", "read,write", "read,write"));
        var request = new LlmInferenceRequest([LlmMessage.User("read the note")], correlation: correlation);
        var durableBoundaryObserved = false;

        var response = await client.GenerateAsync(
            request,
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                durableBoundaryObserved = true;
                Assert.DoesNotContain(transport.Writes, IsTurnStart);
                await commitTransportWrite(token);
            });

        Assert.Equal("The note says tool-visible note.", response.OutputText);
        Assert.True(durableBoundaryObserved);
        var toolResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":99", StringComparison.Ordinal));
        Assert.Contains("\"success\":true", toolResponse, StringComparison.Ordinal);
        Assert.Contains("tool-visible note", toolResponse, StringComparison.Ordinal);
        using var threadStartDocument = JsonDocument.Parse(transport.Writes.Single(IsThreadStart));
        var toolSpecs = threadStartDocument.RootElement.GetProperty("params").GetProperty("dynamicTools");
        var toolSpec = Assert.Single(toolSpecs.EnumerateArray());
        Assert.Equal("command", toolSpec.GetProperty("name").GetString());
        var commandEnum = toolSpec.GetProperty("inputSchema").GetProperty("properties").GetProperty("command").GetProperty("enum").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        Assert.Contains("read", commandEnum);
        Assert.Contains("write", commandEnum);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("llm.inference.start", auditText, StringComparison.Ordinal);
        Assert.Contains("llm.inference.complete", auditText, StringComparison.Ordinal);
        Assert.Contains("llm.appserver.request", auditText, StringComparison.Ordinal);
        Assert.Contains("tool.execute", auditText, StringComparison.Ordinal);
        var events = await ReadAuditEventsAsync(workspace);
        var inferenceStart = Assert.Single(events, auditEvent => auditEvent.Action == "llm.inference.start");
        Assert.Equal("provider-attempt-1", GetMetadataString(inferenceStart, "request_id"));
        Assert.Equal("provider-attempt-1", GetMetadataString(inferenceStart, "provider_attempt_id"));
        Assert.Equal("provider-correlation-1", GetMetadataString(inferenceStart, "provider_correlation_id"));
        Assert.Equal("run-1", GetMetadataString(inferenceStart, "run_id"));
        var appServerToolCall = Assert.Single(events, auditEvent => auditEvent.Action == "llm.appserver.request" && GetMetadataString(auditEvent, "call_id") == "call-1");
        Assert.Equal("call-1", GetMetadataString(appServerToolCall, "tool_request_correlation_id"));
        Assert.Equal("provider-attempt-1", GetMetadataString(appServerToolCall, "provider_attempt_id"));
        Assert.Equal("provider-correlation-1", GetMetadataString(appServerToolCall, "provider_correlation_id"));
        Assert.Equal("run-1", GetMetadataString(appServerToolCall, "run_id"));
        Assert.All(events.Where(auditEvent => auditEvent.Action.StartsWith("tool.", StringComparison.Ordinal)), auditEvent =>
        {
            Assert.Equal("call-1", GetMetadataString(auditEvent, "tool_request_correlation_id"));
            Assert.Equal("run-1", GetMetadataString(auditEvent, "run_id"));
            Assert.Equal("provider-correlation-1", GetMetadataString(auditEvent, "attempt_correlation_id"));
        });
    }

    [Fact]
    public async Task GenerateAsync_advertises_only_loop_assigned_workspace_commands()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var loop = LoopDefinition.CreateDefaultConversation() with { CapabilityIds = [LoopCapabilityIds.WorkspaceCommandFor(ToolCommand.Read)] };
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), loop);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"done","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, broker, workspace.RootPath);

        _ = await client.GenerateAsync(LlmInferenceRequest.FromUserText("hello"), (_, _) => Task.CompletedTask);

        using var threadStartDocument = JsonDocument.Parse(transport.Writes.Single(IsThreadStart));
        var parameters = threadStartDocument.RootElement.GetProperty("params");
        var developerInstructions = parameters.GetProperty("developerInstructions").GetString();
        var toolSpec = Assert.Single(parameters.GetProperty("dynamicTools").EnumerateArray());
        var commandEnum = toolSpec.GetProperty("inputSchema").GetProperty("properties").GetProperty("command").GetProperty("enum").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        Assert.Equal(["read"], commandEnum);
        Assert.Equal(EmbodySenseDeveloperInstructions.Create([ToolCommand.Read]), developerInstructions);
    }

    [Fact]
    public async Task GenerateAsync_omits_dynamic_tools_and_denies_stale_tool_calls_when_loop_grants_no_workspace_commands()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var loop = LoopDefinition.CreateDefaultConversation() with { CapabilityIds = [LoopCapabilityIds.ProviderInference] };
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt(), loop);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(99, "item/tool/call", """{"threadId":"thread-1","turnId":"turn-1","callId":"call-1","namespace":"embodysense","tool":"command","arguments":{"command":"read","path":"shared/note.txt"}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"done","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, broker, workspace.RootPath);

        _ = await client.GenerateAsync(LlmInferenceRequest.FromUserText("hello"), (_, _) => Task.CompletedTask);

        using var threadStartDocument = JsonDocument.Parse(transport.Writes.Single(IsThreadStart));
        var parameters = threadStartDocument.RootElement.GetProperty("params");
        Assert.False(parameters.TryGetProperty("dynamicTools", out _));
        Assert.Equal(EmbodySenseDeveloperInstructions.Create(), parameters.GetProperty("developerInstructions").GetString());
        var toolResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":99", StringComparison.Ordinal));
        Assert.Contains("\"success\":false", toolResponse, StringComparison.Ordinal);
        Assert.Contains("does not grant", toolResponse, StringComparison.Ordinal);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("tool.loop_authority.evaluate", auditText, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"denied\"", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("tool.permission.evaluate", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_rejects_old_per_command_dynamic_tool_names()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(99, "item/tool/call", """{"threadId":"thread-1","turnId":"turn-1","callId":"call-1","namespace":"embodysense","tool":"read","arguments":{"path":"shared/note.txt"}}"""),
            Notification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","delta":"Use embodysense.command."}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"Use embodysense.command.","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, broker, workspace.RootPath);

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("read the note"), (_, _) => Task.CompletedTask);

        Assert.Equal("Use embodysense.command.", response.OutputText);
        var toolResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":99", StringComparison.Ordinal));
        Assert.Contains("\"success\":false", toolResponse, StringComparison.Ordinal);
        Assert.Contains("Unsupported EmbodySense dynamic tool: read", toolResponse, StringComparison.Ordinal);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("llm.appserver.request", auditText, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"failed\"", auditText, StringComparison.Ordinal);
        Assert.Contains("Rejected Codex app-server dynamic tool request.", auditText, StringComparison.Ordinal);
        Assert.Contains("\"arguments_path\":\"shared/note.txt\"", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("tool.execute", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_declines_native_app_server_approval_requests()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(44, "item/commandExecution/requestApproval", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","command":"dotnet test","cwd":"C:\\tmp","reason":"native command"}"""),
            Notification("item/agentMessage/delta", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-2","delta":"I cannot run that native command."}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-2","type":"agentMessage","text":"I cannot run that native command.","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, workingDirectory: workspace.RootPath);

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("run a native command"), (_, _) => Task.CompletedTask);

        Assert.Equal("I cannot run that native command.", response.OutputText);
        var approvalResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":44", StringComparison.Ordinal));
        Assert.Contains("\"decision\":\"decline\"", approvalResponse, StringComparison.Ordinal);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("llm.appserver.request", auditText, StringComparison.Ordinal);
        Assert.Contains("item/commandExecution/requestApproval", auditText, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"denied\"", auditText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("item/fileChange/requestApproval", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","path":"note.txt"}""", "\"decision\":\"decline\"")]
    [InlineData("applyPatchApproval", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","patch":"diff"}""", "\"decision\":\"decline\"")]
    [InlineData("execCommandApproval", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","command":"dotnet test"}""", "\"decision\":\"decline\"")]
    [InlineData("item/permissions/requestApproval", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}""", "\"strictAutoReview\":true")]
    [InlineData("mcpServer/elicitation/request", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}""", "\"action\":\"decline\"")]
    [InlineData("item/tool/requestUserInput", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}""", "\"answers\":{}")]
    public async Task GenerateAsync_declines_other_native_app_server_requests(string method, string parameters, string expectedResponseFragment)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(44, method, parameters),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-2","type":"agentMessage","text":"native request declined","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, workingDirectory: workspace.RootPath);

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("native request"), (_, _) => Task.CompletedTask);

        Assert.Equal("native request declined", response.OutputText);
        var nativeResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":44", StringComparison.Ordinal));
        Assert.Contains(expectedResponseFragment, nativeResponse, StringComparison.Ordinal);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains(method, auditText, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"denied\"", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_rejects_unsupported_app_server_requests_with_json_rpc_error()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(44, "unsupported/nativeRequest", """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-2","type":"agentMessage","text":"unsupported request rejected","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport, workingDirectory: workspace.RootPath);

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("unsupported request"), (_, _) => Task.CompletedTask);

        Assert.Equal("unsupported request rejected", response.OutputText);
        var errorResponse = Assert.Single(transport.Writes, line => line.Contains("\"id\":44", StringComparison.Ordinal));
        Assert.Contains("\"error\"", errorResponse, StringComparison.Ordinal);
        Assert.Contains("does not support app-server request method", errorResponse, StringComparison.Ordinal);
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("\"outcome\":\"failed\"", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_sends_restored_context_as_lower_authority_turn_input()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"new answer","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport);
        var request = new LlmInferenceRequest(
        [
            LlmMessage.System("startup context"),
            LlmMessage.User("old question"),
            LlmMessage.Assistant("old answer"),
            LlmMessage.User("new question")
        ]);

        await client.GenerateAsync(request, (_, _) => Task.CompletedTask);

        using var threadStartDocument = JsonDocument.Parse(transport.Writes.Single(IsThreadStart));
        var developerInstructions = threadStartDocument.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString();
        Assert.Contains("EmbodySense governs the user workspace", developerInstructions);
        Assert.DoesNotContain("startup context", developerInstructions);
        Assert.DoesNotContain("old question", developerInstructions);
        Assert.DoesNotContain("old answer", developerInstructions);
        Assert.DoesNotContain("new question", developerInstructions);
        using var turnStartDocument = JsonDocument.Parse(transport.Writes.Single(IsTurnStart));
        var turnInput = turnStartDocument.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString();
        Assert.Contains("lower-authority reference material", turnInput);
        Assert.Contains("[restored system message]", turnInput);
        Assert.Contains("startup context", turnInput);
        Assert.Contains("old question", turnInput);
        Assert.Contains("old answer", turnInput);
        Assert.Contains("Current user message:", turnInput);
        Assert.Contains("new question", turnInput);
    }

    [Fact]
    public async Task GenerateAsync_uses_the_exact_versioned_governance_and_trusted_instruction_channel_for_custom_requests()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"done","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport);
        var governance = EmbodySenseDeveloperInstructions.Capture();
        var trusted = new[]
        {
            new EmbodySenseTrustedInstruction("nearest-agents", "role instruction content"),
            new EmbodySenseTrustedInstruction("step-one", "authored node instruction")
        };
        var exactLowerAuthorityContext = "context-head-" + new string('x', 25_000) + "-context-tail";
        var request = new LlmInferenceRequest(
            [LlmMessage.User(exactLowerAuthorityContext), LlmMessage.User("explicit current custom-loop turn input")],
            instructionContext: new LlmInferenceInstructionContext(governance, trusted, preserveExactLogicalContext: true));

        await client.GenerateAsync(request, (_, _) => Task.CompletedTask);

        using var threadStartDocument = JsonDocument.Parse(transport.Writes.Single(IsThreadStart));
        var developerInstructions = threadStartDocument.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString();
        Assert.Equal(EmbodySenseDeveloperInstructions.Compose(governance, trusted), developerInstructions);
        Assert.Equal(EmbodySenseDeveloperInstructions.CurrentVersion, governance.Version);
        Assert.DoesNotContain("context-head", developerInstructions, StringComparison.Ordinal);
        using var turnStartDocument = JsonDocument.Parse(transport.Writes.Single(IsTurnStart));
        var turnInput = turnStartDocument.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString();
        Assert.Contains("context-head", turnInput, StringComparison.Ordinal);
        Assert.Contains("context-tail", turnInput, StringComparison.Ordinal);
        Assert.Contains("explicit current custom-loop turn input", turnInput, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted", turnInput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_tampered_custom_governance_snapshot_before_provider_thread_creation()
    {
        var transport = new ScriptedAppServerTransport(Response(1, """{"serverInfo":{}}"""));
        var client = CreateClient(transport);
        var governance = EmbodySenseDeveloperInstructions.Capture() with { Content = "forged governance" };
        var request = new LlmInferenceRequest(
            [LlmMessage.User("explicit current custom-loop turn input")],
            instructionContext: new LlmInferenceInstructionContext(governance, []));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(request));

        Assert.Contains("altered", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, IsThreadStart);
    }

    [Fact]
    public async Task GenerateAsync_rejects_unknown_sandbox_modes()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""));
        var client = new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-test",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CodexSandbox = "unknown-mode"
        }, codexAppServerTransport: transport);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("Unsupported Codex sandbox mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_reports_initialize_errors()
    {
        var transport = new ScriptedAppServerTransport("""{"id":1,"error":{"message":"init denied"}}""");
        var providerRequestStarted = false;
        var client = CreateClient(transport, providerRequestStarted: () => providerRequestStarted = true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("init denied", exception.Message, StringComparison.Ordinal);
        Assert.False(providerRequestStarted);
    }

    [Fact]
    public async Task GenerateAsync_marks_durable_dispatch_when_the_turn_transport_write_fails()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteFailure = line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal) ? new IOException("turn write failed") : null
        };
        var durableDispatchStarted = false;
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<IOException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                durableDispatchStarted = true;
                await commitTransportWrite(token);
            }));

        Assert.Equal("turn write failed", exception.Message);
        Assert.True(durableDispatchStarted);
    }

    [Fact]
    public async Task GenerateAsync_governed_overload_rejects_a_null_boundary_before_provider_activity()
    {
        var providerRequestStarted = false;
        var transport = new ScriptedAppServerTransport();
        await using var client = CreateRawClient(transport, providerRequestStarted: () => providerRequestStarted = true);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            providerTransportCommitBoundary: null!));

        Assert.Equal("providerTransportCommitBoundary", exception.ParamName);
        Assert.Empty(transport.Writes);
        Assert.False(providerRequestStarted);
    }

    [Fact]
    public async Task GenerateAsync_preserves_legacy_dispatch_notification_when_the_turn_write_fails()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteFailure = line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal) ? new IOException("turn write failed") : null
        };
        var providerRequestStarted = false;
        var client = CreateClient(transport, providerRequestStarted: () => providerRequestStarted = true);

        var exception = await Assert.ThrowsAsync<IOException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Equal("turn write failed", exception.Message);
        Assert.True(providerRequestStarted);
    }

    [Fact]
    public async Task GenerateAsync_bounds_a_non_draining_post_checkpoint_turn_write_and_quarantines_the_transport()
    {
        var turnWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteOverride = (line, _) =>
            {
                if (IsTurnStart(line))
                {
                    turnWriteStarted.TrySetResult();
                    return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                }
                return Task.CompletedTask;
            }
        };
        await using var client = CreateRawClient(transport);
        var durableDispatchStarted = false;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                durableDispatchStarted = true;
                await commitTransportWrite(token);
            }));

        await turnWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(durableDispatchStarted);
        Assert.Contains("server-owned deadline", exception.Message, StringComparison.Ordinal);
        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("must not reuse")));
        Assert.Equal(1, transport.Writes.Count(IsTurnStart));
    }

    [Fact]
    public async Task GenerateAsync_treats_caller_cancellation_during_a_non_draining_post_checkpoint_write_as_ambiguous()
    {
        using var cancellation = new CancellationTokenSource();
        var turnWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteOverride = (line, _) =>
            {
                if (IsTurnStart(line))
                {
                    turnWriteStarted.TrySetResult();
                    return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                }
                return Task.CompletedTask;
            }
        };
        await using var client = CreateRawClient(transport);
        var durableDispatchStarted = false;
        var generation = client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            cancellation.Token,
            async (commitTransportWrite, token) =>
            {
                durableDispatchStarted = true;
                await commitTransportWrite(token);
            });

        await turnWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(durableDispatchStarted);
        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("must not reuse")));
    }

    [Fact]
    public async Task GenerateAsync_audits_an_unexpected_late_post_checkpoint_write_fault_without_recording_its_message()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteOverride = (line, _) =>
            {
                if (IsTurnStart(line))
                {
                    writeStarted.TrySetResult();
                    return lateWrite.Task;
                }

                return Task.CompletedTask;
            }
        };
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));
        await using var client = CreateRawClient(transport, auditLog);
        using var cancellation = new CancellationTokenSource();
        var generation = client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            cancellation.Token,
            (commitTransportWrite, token) => commitTransportWrite(token));

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        lateWrite.TrySetException(new ApplicationException("late write input must stay private"));

        var auditEvent = await WaitForLateTransportAuditAsync(auditLog, "write");
        Assert.Equal("ApplicationException", GetMetadataString(auditEvent, "error_type"));
        Assert.DoesNotContain("late write input must stay private", JsonSerializer.Serialize(auditEvent));
    }

    [Fact]
    public async Task GenerateAsync_audits_an_unexpected_detached_transport_disposal_fault_without_recording_its_message()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            DisposeFailure = new ApplicationException("transport disposal input must stay private"),
            WriteOverride = (line, token) =>
            {
                if (IsTurnStart(line))
                {
                    writeStarted.TrySetResult();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return Task.CompletedTask;
            }
        };
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));
        await using var client = CreateRawClient(transport, auditLog);
        var generation = client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            cancellation.Token,
            (commitTransportWrite, token) => commitTransportWrite(token));

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);

        var auditEvent = await WaitForLateTransportAuditAsync(auditLog, "dispose");
        Assert.Equal("ApplicationException", GetMetadataString(auditEvent, "error_type"));
        Assert.DoesNotContain("transport disposal input must stay private", JsonSerializer.Serialize(auditEvent));
    }

    [Fact]
    public async Task GenerateAsync_bounds_a_late_transport_fault_audit_when_the_audit_sink_does_not_drain()
    {
        using var cancellation = new CancellationTokenSource();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteOverride = (line, _) =>
            {
                if (IsTurnStart(line))
                {
                    writeStarted.TrySetResult();
                    return lateWrite.Task;
                }

                return Task.CompletedTask;
            }
        };
        var auditLog = new BlockingAuditLog();
        await using var client = CreateRawClient(transport, auditLog);
        var generation = client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            cancellation.Token,
            (commitTransportWrite, token) => commitTransportWrite(token));

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        lateWrite.TrySetException(new ApplicationException("late write input must stay private"));

        await auditLog.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await auditLog.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task GenerateAsync_fences_request_correlated_setup_before_turn_start_and_preserves_request_start_timing()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            AfterWrite = line =>
            {
                if (IsThreadStart(line))
                {
                    cancellation.Cancel();
                }
            }
        };
        var providerRequestStarted = false;
        var client = CreateClient(transport, providerRequestStarted: () => providerRequestStarted = true);
        var durableDispatchStarted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            cancellation.Token,
            async (commitTransportWrite, token) =>
            {
                durableDispatchStarted = true;
                await commitTransportWrite(token);
            }));

        Assert.True(durableDispatchStarted);
        Assert.False(providerRequestStarted);
        Assert.DoesNotContain(transport.Writes, IsTurnStart);
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task GenerateAsync_does_not_write_the_turn_when_the_durable_boundary_callback_fails()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<IOException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            (_, _) => Task.FromException(new IOException("durable checkpoint unavailable"))));

        Assert.Equal("durable checkpoint unavailable", exception.Message);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task GenerateAsync_commits_the_turn_transport_write_inside_the_durable_boundary()
    {
        var boundaryActive = false;
        var writeObservedOutsideBoundary = false;
        var providerRequestStarted = false;
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"done","phase":"final_answer"}]}}"""))
        {
            AfterWrite = line =>
            {
                if (!boundaryActive)
                {
                    writeObservedOutsideBoundary = true;
                }
            }
        };
        var client = CreateClient(transport, providerRequestStarted: () => providerRequestStarted = true);

        var response = await client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                Assert.Empty(transport.Writes);
                Assert.False(providerRequestStarted);
                boundaryActive = true;
                try
                {
                    await commitTransportWrite(token);
                    Assert.Contains(transport.Writes, IsInitialize);
                    Assert.Contains(transport.Writes, IsInitialized);
                    Assert.Contains(transport.Writes, IsThreadStart);
                    Assert.Contains(transport.Writes, IsTurnStart);
                    Assert.False(providerRequestStarted);
                }
                finally
                {
                    boundaryActive = false;
                }
            });

        Assert.Equal("done", response.OutputText);
        Assert.False(writeObservedOutsideBoundary);
        Assert.False(boundaryActive);
        Assert.True(providerRequestStarted);
        Assert.Equal(1, transport.Writes.Count(IsTurnStart));
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_durable_boundary_that_returns_without_committing_the_turn_write()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[{"id":"item-1","type":"agentMessage","text":"done","phase":"final_answer"}]}}"""));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            (_, _) => Task.CompletedTask));

        Assert.Contains("without invoking its write callback", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.False(transport.Disposed);

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("retry safely"));

        Assert.Equal("done", response.OutputText);
        Assert.Equal(1, GetRequestId(transport.Writes.Single(IsInitialize)));
        Assert.Equal(2, GetRequestId(transport.Writes.Single(IsThreadStart)));
        Assert.Equal(3, GetRequestId(transport.Writes.Single(IsTurnStart)));
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_late_dispatch_callback_without_provider_writes_and_quarantines_the_transport()
    {
        var transport = new ScriptedAppServerTransport();
        await using var client = CreateRawClient(transport);
        Func<CancellationToken, Task>? capturedCommit = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            (commitProviderDispatch, _) =>
            {
                capturedCommit = commitProviderDispatch;
                return Task.CompletedTask;
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => capturedCommit!(CancellationToken.None));
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("after its boundary returns", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_second_turn_write_and_quarantines_the_ambiguous_transport()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""));
        await using var client = CreateRawClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                await commitTransportWrite(token);
                await commitTransportWrite(token);
            }));

        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("at most once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.Writes.Count(IsTurnStart));
        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("must not reuse")));
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_caught_second_turn_write_and_quarantines_the_ambiguous_transport()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""));
        await using var client = CreateRawClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            async (commitTransportWrite, token) =>
            {
                await commitTransportWrite(token);
                try
                {
                    await commitTransportWrite(token);
                }
                catch (InvalidOperationException)
                {
                }
            }));

        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("at most once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.Writes.Count(IsTurnStart));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_boundary_that_returns_before_its_turn_write_completes()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""))
        {
            WriteOverride = (line, _) =>
            {
                if (!IsTurnStart(line))
                {
                    return Task.CompletedTask;
                }

                writeStarted.TrySetResult();
                return releaseWrite.Task;
            }
        };
        await using var client = CreateRawClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            LlmInferenceRequest.FromUserText("hello"),
            responseChunkHandler: null,
            CancellationToken.None,
            (commitTransportWrite, token) =>
            {
                _ = commitTransportWrite(token);
                return Task.CompletedTask;
            }));

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseWrite.TrySetResult();
        await transport.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("before its write callback completed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.Writes.Count(IsTurnStart));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task GenerateAsync_reports_missing_thread_ids()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{}}"""));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("thread id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_reports_failed_turns()
    {
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Notification("turn/completed", """{"threadId":"thread-1","turn":{"id":"turn-1","status":"failed","error":{"message":"model refused"},"items":[]}}"""));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<LlmInferenceTerminalFailureException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("model refused", exception.Message, StringComparison.Ordinal);
        Assert.Equal("turn-1", exception.ProviderResponseId);
    }

    [Fact]
    public async Task GenerateAsync_rejects_oversized_protocol_messages()
    {
        var oversizedLine = "{\"id\":1,\"result\":{\"serverInfo\":\"" + new string('x', 1_000_001) + "\"}}";
        var transport = new ScriptedAppServerTransport(oversizedLine);
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("protocol message exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_reports_malformed_protocol_json()
    {
        var transport = new ScriptedAppServerTransport("not-json");
        var client = CreateClient(transport);

        await Assert.ThrowsAnyAsync<JsonException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));
    }

    [Fact]
    public async Task GenerateAsync_reports_transport_closure_with_error_output()
    {
        var transport = new ScriptedAppServerTransport { ErrorOutput = "transport failed" };
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("transport failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_disposes_app_server_transport()
    {
        var transport = new ScriptedAppServerTransport();
        var client = CreateClient(transport);

        await client.DisposeAsync();

        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task QuarantineAsync_disposes_and_permanently_rejects_reuse_of_an_ambiguous_injected_transport()
    {
        var transport = new ScriptedAppServerTransport();
        var client = CreateClient(transport);

        await client.QuarantineAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("must not reuse")));

        Assert.True(transport.Disposed);
        Assert.Contains("quarantined", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Writes);
    }

    private static LlmInferenceClient CreateClient(
        ScriptedAppServerTransport transport,
        IToolBroker? broker = null,
        string? workingDirectory = null,
        Action? providerRequestStarted = null)
    {
        return new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-test",
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            CodexSandbox = "read-only"
        }, broker, transport, providerRequestStarted);
    }

    private static CodexAppServerInferenceClient CreateRawClient(
        ScriptedAppServerTransport transport,
        IAuditLog? auditLog = null,
        Action? providerRequestStarted = null)
    {
        return new CodexAppServerInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-test",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CodexSandbox = "read-only"
        }, transport: transport, auditLog: auditLog, providerRequestStarted: providerRequestStarted);
    }

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt prompt, LoopDefinition? loopDefinition = null)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = new PermissionPolicyStore().Load(paths);
        ICapabilityAuthorityTransaction authority = new CapabilityAuthorityTransaction(paths);
        var workspaceClient = new LocalWorkspaceClient(paths, new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority));
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt, workspaceClient, new AuditLog(paths), loopDefinition ?? LoopDefinition.CreateDefaultConversation(), new ToolResultRetentionStore(paths));
    }

    private static string Response(int id, string result)
    {
        return $$"""{"id":{{id}},"result":{{result}}}""";
    }

    private static string Notification(string method, string parameters)
    {
        return $$"""{"method":"{{method}}","params":{{parameters}}}""";
    }

    private static string Request(int id, string method, string parameters)
    {
        return $$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}""";
    }

    private static bool IsThreadStart(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "thread/start";
    }

    private static bool IsInitialize(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "initialize";
    }

    private static bool IsInitialized(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "initialized";
    }

    private static bool IsTurnStart(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "turn/start";
    }

    private static int GetRequestId(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task<IReadOnlyList<AuditEvent>> ReadAuditEventsAsync(TestWorkspace workspace)
    {
        var path = new WorkspacePaths(workspace.RootPath).EventsLogPath;
        var events = new List<AuditEvent>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonSerializer.Deserialize<AuditEvent>(line, _auditJsonOptions)!);
            }
        }

        return events;
    }

    private static async Task<AuditEvent> WaitForLateTransportAuditAsync(IAuditLog auditLog, string operation)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var auditEvent = (await auditLog.ReadTailAsync(20)).LastOrDefault(item =>
                item.Action == AuditSchema.Actions.LlmAppServerRequest
                && item.Target == "turn/start"
                && GetMetadataString(item, "operation") == operation);
            if (auditEvent is not null)
            {
                return auditEvent;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for the detached {operation} transport audit event.");
    }

    private static string? GetMetadataString(AuditEvent auditEvent, string key)
    {
        return auditEvent.Metadata.TryGetValue(key, out var value)
            ? value switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                JsonElement element => element.ToString(),
                _ => value?.ToString()
            }
            : null;
    }

    private sealed class ScriptedAppServerTransport(params string[] reads) : ICodexAppServerTransport
    {
        private readonly Queue<string> _reads = new(reads);

        public string ErrorOutput { get; init; } = "";

        public List<string> Writes { get; } = [];

        public Func<string, Exception?>? WriteFailure { get; init; }

        public Func<string, CancellationToken, Task>? WriteOverride { get; init; }

        public Exception? DisposeFailure { get; init; }

        public Action<string>? AfterWrite { get; init; }

        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposedSignal.TrySetResult();
            if (DisposeFailure is not null)
            {
                return ValueTask.FromException(DisposeFailure);
            }

            return ValueTask.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_reads.Count == 0 ? null : _reads.Dequeue());
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            if (WriteFailure?.Invoke(line) is { } exception)
            {
                throw exception;
            }

            Writes.Add(line);
            AfterWrite?.Invoke(line);
            if (WriteOverride is not null)
            {
                return WriteOverride(line, cancellationToken);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingAuditLog : IAuditLog
    {
        public TaskCompletionSource AppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            AppendStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>([]);
        }
    }

    private sealed class ThrowingApprovalPrompt : IToolApprovalPrompt
    {
        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Approval prompt should not have been called.");
        }
    }
}
