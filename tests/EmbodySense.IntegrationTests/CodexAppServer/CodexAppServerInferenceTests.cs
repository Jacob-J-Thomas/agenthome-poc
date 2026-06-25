using System.Text.Json;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.CodexAppServer;

public sealed class CodexAppServerInferenceTests
{
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
        var client = CreateClient(transport);
        var chunks = new List<string>();

        var response = await client.GenerateAsync(LlmInferenceRequest.FromUserText("say hello"), (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal(["hello ", "world"], chunks);
        Assert.Equal("hello world", response.OutputText);
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
        await File.WriteAllTextAsync(workspace.File("workspace", "shared", "note.txt"), "tool-visible note");
        var broker = CreateBroker(workspace, new ThrowingApprovalPrompt());
        var transport = new ScriptedAppServerTransport(
            Response(1, """{"serverInfo":{}}"""),
            Response(2, """{"thread":{"id":"thread-1"}}"""),
            Response(3, """{"turn":{"id":"turn-1","status":"inProgress","items":[]}}"""),
            Request(99, "item/tool/call", """{"threadId":"thread-1","turnId":"turn-1","callId":"call-1","namespace":"embodysense","tool":"command","arguments":{"command":"read","path":"workspace/shared/note.txt"}}"""),
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
        var auditText = await File.ReadAllTextAsync(new WorkspacePaths(workspace.RootPath).EventsLogPath);
        Assert.Contains("llm.inference.start", auditText, StringComparison.Ordinal);
        Assert.Contains("llm.inference.complete", auditText, StringComparison.Ordinal);
        Assert.Contains("llm.appserver.request", auditText, StringComparison.Ordinal);
        Assert.Contains("tool.execute", auditText, StringComparison.Ordinal);
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
            Request(99, "item/tool/call", """{"threadId":"thread-1","turnId":"turn-1","callId":"call-1","namespace":"embodysense","tool":"read","arguments":{"path":"workspace/shared/note.txt"}}"""),
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
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        Assert.Contains("init denied", exception.Message, StringComparison.Ordinal);
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
        string? workingDirectory = null)
    {
        return new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-test",
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            CodexSandbox = "read-only"
        }, broker, transport);
    }

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt prompt)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = new PermissionPolicyStore().Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt, new LocalWorkspaceClient(paths), new AuditLog(paths));
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

    private sealed class ScriptedAppServerTransport(params string[] reads) : ICodexAppServerTransport
    {
        private readonly Queue<string> _reads = new(reads);

        public string ErrorOutput { get; init; } = "";

        public List<string> Writes { get; } = [];

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
