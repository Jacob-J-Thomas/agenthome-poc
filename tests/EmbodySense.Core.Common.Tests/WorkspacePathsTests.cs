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
        Assert.Equal(Path.Combine(paths.RootPath, "workspace"), paths.WorkspacePath);
    }
}
