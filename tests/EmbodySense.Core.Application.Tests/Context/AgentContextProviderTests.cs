using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Context;

public sealed class AgentContextProviderTests
{
    [Fact]
    public async Task LoadAsync_labels_role_identity_and_context_sections()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(
            Document("role", ".agent/ROLE.md", WorkspaceContextDocumentKind.RoleInstruction, "role guide"),
            Document("soul", ".agent/SOUL.md", WorkspaceContextDocumentKind.AgentIdentity, "stable identity"),
            Document("memory", ".agent/MEMORY.md", WorkspaceContextDocumentKind.ContextualState, "memory note"));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        Assert.Collection(
            messages,
            policy =>
            {
                Assert.Equal(LlmMessageRole.System, policy.Role);
                Assert.Contains("treat `.agent/MEMORY.md` as the primary place", policy.Content);
                Assert.Contains("Query conversation history only for transcript-specific evidence", policy.Content);
            },
            role =>
            {
                Assert.Equal(LlmMessageRole.System, role.Role);
                Assert.Contains("Trusted role instruction: .agent/ROLE.md", role.Content);
                Assert.Contains("role guide", role.Content);
            },
            identity =>
            {
                Assert.Equal(LlmMessageRole.System, identity.Role);
                Assert.Contains("Trusted durable agent identity: .agent/SOUL.md", identity.Content);
                Assert.Contains("stable identity", identity.Content);
            },
            memory =>
            {
                Assert.Equal(LlmMessageRole.User, memory.Role);
                Assert.Contains("Lower-authority contextual state: .agent/MEMORY.md", memory.Content);
                Assert.Contains("memory note", memory.Content);
            });
    }

    [Fact]
    public async Task LoadAsync_builds_system_message_from_workspace_agents_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(Document("nearest-agents", "AGENTS.md", WorkspaceContextDocumentKind.RoleInstruction, "repo guide"));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        Assert.Equal(2, messages.Count);
        var message = messages[1];
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

        Assert.Equal(3, messages.Count);
        Assert.Contains("first guide", messages[1].Content);
        Assert.Contains("second guide", messages[2].Content);
    }

    [Fact]
    public async Task LoadAsync_marks_truncated_context_with_character_limit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new FakeWorkspaceContextStore(Document("role", ".agent/ROLE.md", WorkspaceContextDocumentKind.RoleInstruction, new string('x', 12_001)));

        var messages = await new AgentContextProvider(store).LoadAsync(paths);

        var message = Assert.Single(messages, item => item.Content.Contains(".agent/ROLE.md", StringComparison.Ordinal));
        Assert.Contains("[truncated after 12000 characters]", message.Content, StringComparison.Ordinal);
    }

    private static WorkspaceContextDocument Document(string sourceId, string displayPath, WorkspaceContextDocumentKind kind, string content)
    {
        return new WorkspaceContextDocument(sourceId, displayPath, displayPath, kind, content, content.Length, null);
    }

    private sealed class FakeWorkspaceContextStore(params WorkspaceContextDocument[] documents) : IWorkspaceContextStore
    {
        public Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceContextDocument>>(documents);
        }
    }
}
