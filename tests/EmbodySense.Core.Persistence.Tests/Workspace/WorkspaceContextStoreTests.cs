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
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "agent guide");
        await File.WriteAllTextAsync(paths.AgentFile("SOUL.md"), "stable purpose");
        await File.WriteAllTextAsync(paths.AgentFile("PERSONALITY.md"), "interaction style");
        await File.WriteAllTextAsync(paths.AgentFile("MEMORY.md"), "memory note");

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        Assert.Contains(documents, document => document.DisplayPath == ".agent/AGENT.md" && document.Content == "agent guide");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/SOUL.md" && document.Content == "stable purpose");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/PERSONALITY.md" && document.Content == "interaction style");
        Assert.Contains(documents, document => document.DisplayPath == ".agent/MEMORY.md" && document.Content == "memory note");
    }

    [Fact]
    public async Task LoadDocumentsAsync_loads_workspace_agents_file()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "repo guide");
        var paths = new WorkspacePaths(workspace.RootPath);

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var document = Assert.Single(documents);
        Assert.Equal("AGENTS.md", document.DisplayPath);
        Assert.Equal("repo guide", document.Content);
    }

    [Fact]
    public async Task LoadDocumentsAsync_loads_nearest_parent_agents_file()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("scratch"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "parent guide");
        var paths = new WorkspacePaths(workspace.File("scratch"));

        var documents = await new WorkspaceContextStore().LoadDocumentsAsync(paths);

        var document = Assert.Single(documents);
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

        var document = Assert.Single(documents);
        Assert.Equal("workspace guide", document.Content);
    }
}
