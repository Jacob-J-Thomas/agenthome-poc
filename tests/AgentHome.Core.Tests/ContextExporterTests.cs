using AgentHome.Core.Context;
using AgentHome.Core.Tasks;
using AgentHome.Core.Workspace;
using Xunit;

namespace AgentHome.Core.Tests;

public sealed class ContextExporterTests
{
    [Fact]
    public async Task ExportCodexAsyncCreatesHandoffFileWithCoreSections()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        await new WorkspaceInitializer().InitializeAsync(workspace.Root);
        await new TaskStore(paths).StartAsync("Document export context");

        var outputPath = await new ContextExporter(paths).ExportCodexAsync();

        Assert.True(File.Exists(outputPath));

        var content = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Codex context export", content);
        Assert.Contains("## Agent operating guide", content);
        Assert.Contains("## Workspace context", content);
        Assert.Contains("## Memory", content);
        Assert.Contains("## Permissions", content);
        Assert.Contains("## Models", content);
        Assert.Contains("## Latest tasks", content);
        Assert.Contains("Document export context", content);
    }

    [Fact]
    public async Task ExportCodexAsyncAppendsAuditEvent()
    {
        using var workspace = TestWorkspace.Create();
        var paths = new WorkspacePaths(workspace.Root);
        await new WorkspaceInitializer().InitializeAsync(workspace.Root);

        await new ContextExporter(paths).ExportCodexAsync();

        var audit = await File.ReadAllTextAsync(paths.EventsLogPath);
        Assert.Contains("\"Action\":\"context.export\"", audit);
        Assert.Contains("\"Decision\":\"Allow\"", audit);
    }
}
