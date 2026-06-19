using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Context;

public sealed class AgentContextProviderTests
{
    [Fact]
    public async Task LoadAsync_builds_system_message_from_non_empty_agent_documents()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(
            new WorkspaceContextDocument(".agent/AGENT.md", "agent guide"),
            new WorkspaceContextDocument(".agent/MEMORY.md", "memory note"));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Contains(".agent/AGENT.md", message.Content);
        Assert.Contains("agent guide", message.Content);
        Assert.Contains(".agent/MEMORY.md", message.Content);
        Assert.Contains("memory note", message.Content);
        Assert.Contains("treat `.agent/MEMORY.md` as the primary place", message.Content);
        Assert.Contains("Query conversation history only for transcript-specific evidence", message.Content);
    }

    [Fact]
    public async Task LoadAsync_builds_system_message_from_workspace_agents_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(new WorkspaceContextDocument("AGENTS.md", "repo guide"));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Contains("AGENTS.md", message.Content);
        Assert.Contains("repo guide", message.Content);
    }

    [Fact]
    public async Task LoadAsync_returns_empty_when_store_returns_no_documents()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore();

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task LoadAsync_preserves_store_document_order()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(
            new WorkspaceContextDocument("first.md", "first guide"),
            new WorkspaceContextDocument("second.md", "second guide"));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.True(message.Content.IndexOf("first guide", StringComparison.Ordinal) < message.Content.IndexOf("second guide", StringComparison.Ordinal));
    }

    private sealed class FakeWorkspaceContextStore(params WorkspaceContextDocument[] documents) : IWorkspaceContextStore
    {
        public Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceContextDocument>>(documents);
        }
    }
}
