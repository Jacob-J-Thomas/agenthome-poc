using System.Text.Json;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.CodexAppServer;

public sealed class CodexAppServerInferenceTests
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

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
    public async Task GenerateAsync_routes_dynamic_tool_calls_through_tool_broker()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("read the note"), (_, _) => Task.CompletedTask);

        Assert.Equal("The note says tool-visible note.", response.OutputText);
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
        var appServerToolCall = Assert.Single(events, auditEvent => auditEvent.Action == "llm.appserver.request" && GetMetadataString(auditEvent, "call_id") == "call-1");
        Assert.Equal("call-1", GetMetadataString(appServerToolCall, "tool_request_correlation_id"));
        Assert.All(events.Where(auditEvent => auditEvent.Action.StartsWith("tool.", StringComparison.Ordinal)), auditEvent =>
        {
            Assert.Equal("call-1", GetMetadataString(auditEvent, "tool_request_correlation_id"));
        });
    }

    [Fact]
    public async Task GenerateAsync_advertises_only_loop_assigned_workspace_commands()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(request));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
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
    public async Task GenerateAsync_conservatively_reports_dispatch_when_the_turn_write_fails()
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("model refused", exception.Message, StringComparison.Ordinal);
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

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt prompt, LoopDefinition? loopDefinition = null)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = new PermissionPolicyStore().Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt, new LocalWorkspaceClient(paths), new AuditLog(paths), loopDefinition ?? LoopDefinition.CreateDefaultConversation(), new ToolResultRetentionStore(paths));
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

    private static bool IsTurnStart(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "turn/start";
    }

    private static async Task<IReadOnlyList<AuditEvent>> ReadAuditEventsAsync(TestWorkspace workspace)
    {
        var path = new WorkspacePaths(workspace.RootPath).EventsLogPath;
        var events = new List<AuditEvent>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonSerializer.Deserialize<AuditEvent>(line, AuditJsonOptions)!);
            }
        }

        return events;
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

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
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
            return Task.CompletedTask;
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
