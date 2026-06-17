using System.Text.Json;
using EmbodySense.Core.Inference.Implementations;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

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

    [Fact]
    public async Task GenerateAsync_includes_restored_context_in_thread_start_developer_instructions()
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
        Assert.Contains("startup context", developerInstructions);
        Assert.Contains("old question", developerInstructions);
        Assert.Contains("old answer", developerInstructions);
        Assert.DoesNotContain("new question", developerInstructions);
        using var turnStartDocument = JsonDocument.Parse(transport.Writes.Single(IsTurnStart));
        Assert.Equal("new question", turnStartDocument.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
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
        var policy = DirectoryPermissionPolicy.Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt);
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

        public string ErrorOutput => "";

        public List<string> Writes { get; } = [];

        public ValueTask DisposeAsync()
        {
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
