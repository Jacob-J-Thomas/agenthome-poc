using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Workspace;

public sealed class WorkspaceContextStoreTests
{
    [Fact]
    public async Task LoadDocumentsAsync_loads_non_empty_agent_documents()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.AgentFile("ROLE.md"), "workspace role");
        await File.WriteAllTextAsync(paths.AgentFile("SOUL.md"), "stable purpose");
        await File.WriteAllTextAsync(paths.AgentFile("PERSONALITY.md"), "interaction style");
        await File.WriteAllTextAsync(paths.AgentFile("MEMORY.md"), "memory note");

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        Assert.Contains(documents, document => document.DisplayPath == ".agent/ROLE.md" && document.Content == "workspace role");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/SOUL.md" && document.Content == "stable purpose");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/PERSONALITY.md" && document.Content == "interaction style");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/MEMORY.md" && document.Content == "memory note");
        Assert.Equal(7, documents.Count);
        Assert.Equal(["nearest-agents", "role", "soul", "personality", "context", "memory", "models"], documents.Select(document => document.SourceId));
        Assert.All(documents.Take(2), document => Assert.Equal(WorkspaceContextDocumentKind.RoleInstruction, document.Kind));
        Assert.All(documents.Skip(2).Take(2), document => Assert.Equal(WorkspaceContextDocumentKind.IdentityInstruction, document.Kind));
        Assert.All(documents.Skip(4), document => Assert.Equal(WorkspaceContextDocumentKind.ContextualState, document.Kind));
        Assert.All(documents, document => Assert.True(Path.IsPathFullyQualified(document.ExactPath)));
        Assert.All(documents.Where(document => document.Content.Length == 0), document => Assert.NotNull(document.OmissionReason));
    }

    [Fact]
    public async Task LoadDocumentsAsync_prefers_role_file_over_legacy_agent_file()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.AgentFile("ROLE.md"), "current role");
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var role = Assert.Single(documents, document => document.SourceId == "role");
        Assert.Equal(".agent/ROLE.md", role.DisplayPath);
        Assert.Equal("current role", role.Content);
    }

    [Fact]
    public async Task LoadDocumentsAsync_reads_legacy_agent_file_without_mutating_workspace()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var role = Assert.Single(documents, document => document.SourceId == "role");
        Assert.Equal(".agent/AGENT.md", role.DisplayPath);
        Assert.Equal("legacy role", role.Content);
        Assert.False(File.Exists(paths.AgentFile("ROLE.md")));
    }

    [Fact]
    public async Task LoadDocumentsAsync_loads_workspace_agents_file()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "repo guide");
        var paths = new WorkspacePaths(workspace.RootPath);

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var document = documents[0];
        Assert.Equal("AGENTS.md", document.DisplayPath);
        Assert.Equal("repo guide", document.Content);
        Assert.Equal("nearest-agents", document.SourceId);
        Assert.Equal(WorkspaceContextDocumentKind.RoleInstruction, document.Kind);
        Assert.Equal("repo guide".Length, document.OriginalCharacterCount);
        Assert.Null(document.OmissionReason);
    }

    [Fact]
    public async Task LoadDocumentsAsync_loads_nearest_parent_agents_file()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("scratch"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "parent guide");
        var paths = new WorkspacePaths(workspace.File("scratch"));

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var document = documents[0];
        Assert.Equal("../AGENTS.md", document.DisplayPath);
        Assert.Equal("parent guide", document.Content);
    }

    [Fact]
    public async Task LoadDocumentsAsync_prefers_workspace_agents_file_over_parent_agents_file()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("scratch"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "parent guide");
        await File.WriteAllTextAsync(workspace.File("scratch", "AGENTS.md"), "workspace guide");
        var paths = new WorkspacePaths(workspace.File("scratch"));

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var document = documents[0];
        Assert.Equal("workspace guide", document.Content);
    }
}
