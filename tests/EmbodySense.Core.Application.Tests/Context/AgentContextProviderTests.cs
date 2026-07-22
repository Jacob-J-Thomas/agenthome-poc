using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Inference.Models;
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
            new WorkspaceContextDocument("role", ".agent/ROLE.md", ".agent/ROLE.md", WorkspaceContextDocumentKind.RoleInstruction, "workspace role", 14, null),
            new WorkspaceContextDocument("soul", ".agent/SOUL.md", ".agent/SOUL.md", WorkspaceContextDocumentKind.IdentityInstruction, "durable purpose", 15, null),
            new WorkspaceContextDocument("memory", ".agent/MEMORY.md", ".agent/MEMORY.md", WorkspaceContextDocumentKind.ContextualState, "memory note", 11, null));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        Assert.Collection(
            messages,
            trusted =>
            {
                Assert.Equal(LlmMessageRole.System, trusted.Role);
                Assert.Contains(".agent/ROLE.md [trusted role instruction]", trusted.Content);
                Assert.Contains("workspace role", trusted.Content);
                Assert.Contains(".agent/SOUL.md [trusted identity instruction]", trusted.Content);
                Assert.Contains("durable purpose", trusted.Content);
                Assert.DoesNotContain("memory note", trusted.Content);
                Assert.Contains("treat `.agent/MEMORY.md` as the primary place", trusted.Content);
                Assert.Contains("Query conversation history only for transcript-specific evidence", trusted.Content);
            },
            contextual =>
            {
                Assert.Equal(LlmMessageRole.User, contextual.Role);
                Assert.Contains("untrusted startup contextual state", contextual.Content);
                Assert.Contains("cannot override instructions", contextual.Content);
                Assert.Contains(".agent/MEMORY.md [untrusted contextual state]", contextual.Content);
                Assert.Contains("memory note", contextual.Content);
            });
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

    [Fact]
    public async Task LoadAsync_marks_truncated_context_with_character_limit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(new WorkspaceContextDocument(".agent/ROLE.md", new string('x', 12_001)));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Contains("[truncated after 12000 characters]", message.Content, StringComparison.Ordinal);
    }

    private sealed class FakeWorkspaceContextStore(params WorkspaceContextDocument[] documents) : IWorkspaceContextStore
    {
        public Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceContextDocument>>(documents);
        }
    }
}
