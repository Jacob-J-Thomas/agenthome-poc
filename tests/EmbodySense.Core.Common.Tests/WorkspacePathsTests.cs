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
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions", "custom"), paths.CustomLoopDefinitionsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions", "custom-tombstones"), paths.CustomLoopDefinitionTombstonesPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions", "custom-create-operations"), paths.CustomLoopDefinitionOperationsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "runs"), paths.LoopRunsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "runs", "custom"), paths.CustomLoopRunsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "runs", "custom-trace-deletion-operations"), paths.CustomLoopTraceDeletionOperationsPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "loops", "definitions", "default-conversation.json"), paths.DefaultConversationLoopDefinitionPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "memory", "conversations", ".workspace-turn.lock"), paths.ConversationTurnLockPath);
        Assert.Equal(Path.Combine(paths.AgentPath, "ROLE.md"), paths.RolePath);
        Assert.Equal(Path.Combine(paths.RootPath, "shared"), paths.WorkspaceSharedPath);
        Assert.Equal(Path.Combine(paths.RootPath, "private"), paths.WorkspacePrivatePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void File_helpers_canonicalize_valid_nested_descendants(bool agentFile)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = agentFile ? paths.AgentPath : paths.WorkspacePath;

        var result = Resolve(paths, agentFile, Path.Combine("nested", ".", "child", "..", "file.txt"));

        Assert.Equal(Path.Combine(root, "nested", "file.txt"), result);
        Assert.True(Path.IsPathFullyQualified(result));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void File_helpers_reject_rooted_empty_and_escaping_paths(bool agentFile)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = agentFile ? paths.AgentPath : paths.WorkspacePath;
        var siblingName = Path.GetFileName(root) + "-sibling";
        var siblingPrefixEscape = Path.Combine("..", siblingName, "file.txt");
        var directEscape = agentFile ? Path.Combine("..", "shared", "file.txt") : Path.Combine("..", "outside.txt");

        Assert.Throws<ArgumentNullException>(() => Resolve(paths, agentFile, null!));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, string.Empty));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, " "));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, "."));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, workspace.File("rooted.txt")));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, directEscape));
        Assert.Throws<ArgumentException>(() => Resolve(paths, agentFile, siblingPrefixEscape));
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

    private static string Resolve(WorkspacePaths paths, bool agentFile, string relativePath) => agentFile ? paths.AgentFile(relativePath) : paths.WorkspaceFile(relativePath);
}
