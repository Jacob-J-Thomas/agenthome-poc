using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Workspace;

public sealed class WorkspaceStatusReaderTests
{
    [Fact]
    public async Task Read_returns_paths_initialization_state_and_permission_summary()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.Equal(workspace.RootPath, status.RootPath);
        Assert.True(status.IsInitialized);
        Assert.EndsWith(Path.Combine(".agent", "audit", "events.ndjson"), status.EventsLogPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(".agent", "permissions.json"), status.PermissionsPath, StringComparison.Ordinal);
        Assert.Equal("requires approval for missing or unmatched directory rules", status.DefaultAccess);
        Assert.NotEmpty(status.ApprovedEntries);
        Assert.NotEmpty(status.DeniedEntries);
    }

    [Fact]
    public void Read_reports_missing_permissions_as_default_approval_policy()
    {
        using var workspace = new TestWorkspace();

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.False(status.HasPartialScaffold);
        Assert.Equal("requires approval because permissions.json is missing, invalid, or unsupported", status.DefaultAccess);
        Assert.Empty(status.ApprovedEntries);
        Assert.Empty(status.DeniedEntries);
    }

    [Fact]
    public async Task Read_reports_a_pre_role_workspace_as_uninitialized()
    {
        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, "{}");
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
    }
}
