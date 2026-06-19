using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Core.Clients.LocalWorkspace;

public sealed class LocalWorkspaceClientTests
{
    [Fact]
    public async Task ListAsync_orders_directories_before_files()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var target = workspace.File("workspace", "shared");
        Directory.CreateDirectory(workspace.File("workspace", "shared", "notes"));
        await File.WriteAllTextAsync(Path.Combine(target, "b.txt"), "file");

        var result = await new LocalWorkspaceClient(paths).ListAsync(target);

        Assert.Equal("notes" + Path.DirectorySeparatorChar + Environment.NewLine + "b.txt", result.Text);
        Assert.Equal(2, result.Metadata["entry_count"]);
    }

    [Fact]
    public async Task SearchAsync_returns_workspace_relative_matches()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "alpha" + Environment.NewLine + "needle");

        var result = await new LocalWorkspaceClient(paths).SearchAsync(file, "needle");

        Assert.Contains("workspace" + Path.DirectorySeparatorChar + "shared" + Path.DirectorySeparatorChar + "note.txt:2: needle", result.Text);
        Assert.Equal(1, result.Metadata["match_count"]);
    }
}
