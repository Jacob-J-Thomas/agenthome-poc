using AgentHome.Core.Workspace;
using Xunit;

namespace AgentHome.Core.Tests;

public sealed class WorkspaceInitializerTests
{
    [Fact]
    public async Task InitializeAsyncCreatesRequiredFilesAndDirectories()
    {
        using var workspace = TestWorkspace.Create();
        var initializer = new WorkspaceInitializer();

        await initializer.InitializeAsync(workspace.Root);

        var requiredFiles = new[]
        {
            ".agent/AGENT.md",
            ".agent/CONTEXT.md",
            ".agent/MEMORY.md",
            ".agent/models.json",
            ".agent/tools.json",
            ".agent/permissions.json",
            ".agent/logs/events.ndjson"
        };

        var requiredDirectories = new[]
        {
            ".agent/tasks",
            ".agent/skills",
            ".agent/hooks",
            ".agent/recipes",
            ".agent/exports",
            "workspace/private",
            "workspace/shared",
            "workspace/generated",
            "workspace/system"
        };

        foreach (var relativePath in requiredFiles)
        {
            Assert.True(File.Exists(workspace.Path(relativePath)), $"Expected file to exist: {relativePath}");
        }

        foreach (var relativePath in requiredDirectories)
        {
            Assert.True(Directory.Exists(workspace.Path(relativePath)), $"Expected directory to exist: {relativePath}");
        }
    }

    [Fact]
    public async Task InitializeAsyncDoesNotOverwriteExistingAgentFiles()
    {
        using var workspace = TestWorkspace.Create();
        var contextPath = workspace.Path(".agent/CONTEXT.md");
        Directory.CreateDirectory(Path.GetDirectoryName(contextPath)!);
        await File.WriteAllTextAsync(contextPath, "human-authored context");

        await new WorkspaceInitializer().InitializeAsync(workspace.Root);

        Assert.Equal("human-authored context", await File.ReadAllTextAsync(contextPath));
    }

    [Fact]
    public async Task InitializeAsyncAppendsWorkspaceInitAuditEvent()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);

        await new WorkspaceInitializer().InitializeAsync(workspace.Root);

        var audit = await File.ReadAllTextAsync(paths.EventsLogPath);
        Assert.Contains("\"Action\":\"workspace.init\"", audit);
        Assert.Contains("\"Decision\":\"Allow\"", audit);
    }
}
