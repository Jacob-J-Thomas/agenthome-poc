using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests;

public sealed class WorkspacePathsTests
{
    [Fact]
    public void Constructor_expands_root_and_agent_paths()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(Path.Combine(workspace.RootPath, "."));

        Assert.Equal(Path.GetFullPath(workspace.RootPath), paths.RootPath);
        Assert.Equal(Path.Combine(paths.RootPath, ".agent"), paths.AgentPath);
        Assert.Equal(paths.RootPath, paths.WorkspacePath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops"), paths.LoopsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions"), paths.LoopDefinitionsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "runs"), paths.LoopRunsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions", "default-conversation.json"), paths.DefaultConversationLoopDefinitionPath);
        Assert.Equal(Path.Combine(paths.RootPath, "shared"), paths.WorkspaceSharedPath);
        Assert.Equal(Path.Combine(paths.RootPath, "private"), paths.WorkspacePrivatePath);
    }

    [Fact]
    public async Task WorkspaceInstructionLocator_finds_nearest_agents_file_from_root()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("project", "nested"));
        await File.WriteAllTextAsync(workspace.File("AGENTS.md"), "outer");
        await File.WriteAllTextAsync(workspace.File("project", "AGENTS.md"), "inner");

        var path = WorkspaceInstructionLocator.FindNearest(workspace.File("project", "nested"));

        Assert.Equal(Path.GetFullPath(workspace.File("project", "AGENTS.md")), path);
        Assert.Equal("../AGENTS.md", WorkspaceInstructionLocator.GetDisplayPath(workspace.File("project", "nested"), path!));
    }
}
