using EmbodySense.Core.Context;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

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

        var messages = await new AgentContextProvider().LoadAsync(paths);

        var message = Assert.Single(messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Contains(".agent/AGENT.md", message.Content);
        Assert.Contains("agent guide", message.Content);
        Assert.Contains(".agent/MEMORY.md", message.Content);
        Assert.Contains("memory note", message.Content);
    }
}
