using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Persistence.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Core.Application.Context;

public sealed class AgentContextProviderTests
{
    [Fact]
    public async Task LoadAsync_builds_system_message_from_non_empty_agent_documents()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "agent guide");
        await File.WriteAllTextAsync(paths.AgentFile("MEMORY.md"), "memory note");

        var messages = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths);

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
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "repo guide");
        var paths = new WorkspacePaths(workspace.RootPath);

        var messages = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Contains("AGENTS.md", message.Content);
        Assert.Contains("repo guide", message.Content);
    }

    [Fact]
    public async Task LoadAsync_does_not_load_parent_agents_file()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("scratch"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "parent guide");
        var paths = new WorkspacePaths(workspace.File("scratch"));

        var messages = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task LoadAsync_prefers_workspace_agents_file_over_parent_agents_file()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("scratch"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "parent guide");
        await File.WriteAllTextAsync(workspace.File("scratch", "AGENTS.md"), "workspace guide");
        var paths = new WorkspacePaths(workspace.File("scratch"));

        var messages = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Contains("workspace guide", message.Content);
        Assert.DoesNotContain("parent guide", message.Content);
    }
}
