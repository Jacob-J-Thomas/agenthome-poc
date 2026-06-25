using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Clients.Tests.LocalWorkspace;

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

    [Fact]
    public async Task ReadAsync_truncates_large_files_and_reports_metadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "large.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, new string('a', 121_000));

        var result = await new LocalWorkspaceClient(paths).ReadAsync(file);

        Assert.Contains("[truncated after", result.Text);
        Assert.Equal(true, result.Metadata["truncated"]);
    }

    [Fact]
    public async Task ReadAsync_rejects_binary_files()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "binary.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [65, 0, 66]);

        var exception = await Assert.ThrowsAsync<IOException>(() => new LocalWorkspaceClient(paths).ReadAsync(file));

        Assert.Contains("binary", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_caps_match_count_and_marks_truncation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "many.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 220).Select(index => $"needle {index}"));

        var result = await new LocalWorkspaceClient(paths).SearchAsync(file, "needle");

        Assert.Contains("[truncated after", result.Text);
        Assert.Equal(200, result.Metadata["match_count"]);
        Assert.Equal(true, result.Metadata["truncated"]);
    }

    [Fact]
    public async Task SearchAsync_skips_oversized_files_and_reports_truncation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "large.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, new byte[1_048_577]);

        var result = await new LocalWorkspaceClient(paths).SearchAsync(workspace.File("workspace", "shared"), "needle");

        Assert.Contains("(no matches)", result.Text);
        Assert.Equal(1, result.Metadata["skipped_large_files"]);
        Assert.Equal(true, result.Metadata["truncated"]);
    }

    [Fact]
    public async Task SearchAsync_caps_directory_file_enumeration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var directory = workspace.File("workspace", "shared");
        Directory.CreateDirectory(directory);
        foreach (var index in Enumerable.Range(1, 505))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"note-{index:000}.txt"), "alpha");
        }

        var result = await new LocalWorkspaceClient(paths).SearchAsync(directory, "needle");

        Assert.Contains("[truncated after 500 files", result.Text);
        Assert.Equal(500, result.Metadata["files_scanned"]);
        Assert.Equal(true, result.Metadata["truncated"]);
    }

    [Fact]
    public async Task SearchAsync_truncates_long_matching_lines()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var file = workspace.File("workspace", "shared", "long.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "needle " + new string('x', 600));

        var result = await new LocalWorkspaceClient(paths).SearchAsync(file, "needle");

        Assert.Contains("[line truncated]", result.Text);
        Assert.Equal(1, result.Metadata["match_count"]);
    }
}
