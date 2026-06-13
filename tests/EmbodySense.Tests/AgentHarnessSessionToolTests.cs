using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

public sealed class AgentHarnessSessionToolTests
{
    [Fact]
    public async Task SendUserMessageAsync_executes_model_requested_tool_and_continues_turn()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("workspace", "shared", "note.txt"), "tool-visible note");
        var client = new ScriptedInferenceClient(
            """
            I need to inspect the note.

            ```embodysense-tool
            {"tool":"read","path":"workspace/shared/note.txt"}
            ```
            """,
            "The note says: tool-visible note");
        var session = new AgentHarnessSession(client, CreateBroker(workspace, new ThrowingApprovalPrompt()));

        var response = await session.SendUserMessageAsync("What is in the note?");

        Assert.Equal("The note says: tool-visible note", response.OutputText);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[0], message => message.Role == LlmMessageRole.System && message.Content.Contains("EmbodySense governed tools", StringComparison.Ordinal));
        Assert.Contains(client.Requests[1], message => message.Role == LlmMessageRole.Tool && message.Content.Contains("tool-visible note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendUserMessageAsync_returns_denied_tool_result_to_model()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File("workspace", "private", "secret.txt"), "private note");
        var client = new ScriptedInferenceClient(
            """
            ```embodysense-tool
            {"tool":"read","path":"workspace/private/secret.txt"}
            ```
            """,
            "I cannot read that private file.");
        var session = new AgentHarnessSession(client, CreateBroker(workspace, new ThrowingApprovalPrompt()));

        var response = await session.SendUserMessageAsync("Read the private note.");

        Assert.Equal("I cannot read that private file.", response.OutputText);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1], message => message.Role == LlmMessageRole.Tool && message.Content.Contains("outcome: denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendUserMessageAsync_routes_approval_required_tool_through_prompt()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var prompt = new FixedApprovalPrompt(ToolApprovalResponse.Approve("test", "approved in test"));
        var client = new ScriptedInferenceClient(
            """
            ```embodysense-tool
            {"tool":"write","path":".agent/skills/generated.md","content":"agent skill"}
            ```
            """,
            "I wrote the skill note.");
        var session = new AgentHarnessSession(client, CreateBroker(workspace, prompt));

        var response = await session.SendUserMessageAsync("Create a skill note.");

        Assert.Equal("I wrote the skill note.", response.OutputText);
        Assert.Single(prompt.Requests);
        Assert.Equal("agent skill", await File.ReadAllTextAsync(workspace.File(".agent", "skills", "generated.md")));
        Assert.Contains(client.Requests[1], message => message.Role == LlmMessageRole.Tool && message.Content.Contains("outcome: succeeded", StringComparison.Ordinal));
    }

    private static ToolBroker CreateBroker(TestWorkspace workspace, IToolApprovalPrompt prompt)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var policy = DirectoryPermissionPolicy.Load(paths);
        return new ToolBroker(paths, new ToolPermissionService(paths, policy), prompt);
    }

    private sealed class ScriptedInferenceClient(params string[] outputs) : ILlmInferenceClient
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            var output = _outputs.Dequeue();
            return Task.FromResult(new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex));
        }
    }

    private sealed class ThrowingApprovalPrompt : IToolApprovalPrompt
    {
        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Approval prompt should not have been called.");
        }
    }

    private sealed class FixedApprovalPrompt(ToolApprovalResponse response) : IToolApprovalPrompt
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
