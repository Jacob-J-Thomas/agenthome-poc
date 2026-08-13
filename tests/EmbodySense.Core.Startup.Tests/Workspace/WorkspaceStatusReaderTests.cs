using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Workspace;

[Collection(SharedDefaultCapabilityTrustCollection.Name)]
public sealed class WorkspaceStatusReaderTests
{
    [Fact]
    public async Task Read_returns_paths_initialization_state_and_permission_summary()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.Equal(workspace.RootPath, status.RootPath);
        Assert.True(status.IsInitialized);
        Assert.False(status.RequiresExplicitCleanup);
        Assert.EndsWith(Path.Combine(".agent", "audit", "events.ndjson"), status.EventsLogPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(".agent", "permissions.json"), status.PermissionsPath, StringComparison.Ordinal);
        Assert.Equal("requires approval for missing or unmatched directory rules", status.DefaultAccess);
        Assert.NotEmpty(status.ApprovedEntries);
        Assert.NotEmpty(status.DeniedEntries);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("substituted")]
    [InlineData("inactive")]
    [InlineData("wrong-workspace")]
    [InlineData("source-ineligible")]
    public async Task Read_reports_damaged_default_role_evidence_as_cleanup_required_without_mutation(string damage)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await DefaultContextualRoleEvidenceTestSupport.DamageAsync(workspace, damage);
        var workspaceBefore = DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.RootPath);
        var serverStateBefore = DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.ServerStatePath);

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
        AssertSnapshotsEqual(workspaceBefore, DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.RootPath));
        AssertSnapshotsEqual(serverStateBefore, DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.ServerStatePath));
    }

    [Fact]
    public void Read_reports_missing_permissions_as_default_approval_policy()
    {
        using var workspace = new TestWorkspace();

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.False(status.HasPartialScaffold);
        Assert.False(status.RequiresExplicitCleanup);
        Assert.Equal("requires approval because permissions.json is missing, invalid, or unsupported", status.DefaultAccess);
        Assert.Empty(status.ApprovedEntries);
        Assert.Empty(status.DeniedEntries);
    }

    [Fact]
    public async Task Read_reports_a_pre_role_workspace_as_uninitialized()
    {
        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        File.Delete(paths.RolePath);
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Fact]
    public async Task Read_reports_invalid_permissions_as_a_partial_scaffold()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(".agent", "permissions.json"), "{\"version\":");

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
        Assert.Equal("requires approval because permissions.json is missing, invalid, or unsupported", status.DefaultAccess);
    }

    [Fact]
    public async Task Read_reports_blank_role_as_a_partial_scaffold()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(".agent", "ROLE.md"), " \r\n\t");

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"schemaVersion\":1,\"status\":")]
    [InlineData("{\"schemaVersion\":2,\"status\":\"completed\"}")]
    [InlineData("{\"schemaVersion\":1,\"status\":\"completed\",\"extra\":true}")]
    public async Task Read_keeps_valid_role_and_permissions_partial_until_the_current_completion_marker_exists(string? marker)
    {
        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        if (marker is null)
        {
            File.Delete(paths.WorkspaceInitializationMarkerPath);
        }
        else
        {
            await File.WriteAllTextAsync(paths.WorkspaceInitializationMarkerPath, marker);
        }

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Fact]
    public async Task Read_rejects_an_oversized_completion_marker_without_treating_the_workspace_as_initialized()
    {
        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(paths.WorkspaceInitializationMarkerPath, new string('x', 257));

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Fact]
    public async Task Read_requires_explicit_cleanup_when_the_completion_marker_path_is_a_directory()
    {
        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        File.Delete(paths.WorkspaceInitializationMarkerPath);
        Directory.CreateDirectory(paths.WorkspaceInitializationMarkerPath);

        var status = new WorkspaceStatusReader().Read(workspace.RootPath);

        Assert.False(status.IsInitialized);
        Assert.True(status.HasPartialScaffold);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Fact]
    public async Task Read_requires_explicit_cleanup_when_the_completion_marker_is_read_only_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(paths.WorkspaceInitializationMarkerPath, "invalid");
        File.SetAttributes(paths.WorkspaceInitializationMarkerPath, FileAttributes.ReadOnly);
        try
        {
            var status = new WorkspaceStatusReader().Read(workspace.RootPath);

            Assert.False(status.IsInitialized);
            Assert.True(status.HasPartialScaffold);
            Assert.True(status.RequiresExplicitCleanup);
        }
        finally
        {
            File.SetAttributes(paths.WorkspaceInitializationMarkerPath, FileAttributes.Normal);
        }
    }

    private static void AssertSnapshotsEqual(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var path in expected.Keys)
        {
            Assert.Equal(expected[path], actual[path]);
        }
    }
}
