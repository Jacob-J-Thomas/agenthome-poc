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
        Assert.Equal("requires approval because permissions.json is missing, invalid, or unsupported", status.DefaultAccess);
        Assert.Empty(status.ApprovedEntries);
        Assert.Empty(status.DeniedEntries);
    }
}
