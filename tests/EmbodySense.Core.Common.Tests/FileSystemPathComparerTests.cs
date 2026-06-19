using EmbodySense.Core.Common;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests;

public sealed class FileSystemPathComparerTests
{
    [Fact]
    public void IsWithinOrEqual_returns_true_for_child_path()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.RootPath, "workspace");
        var child = Path.Combine(root, "shared", "note.txt");

        Assert.True(FileSystemPathComparer.IsWithinOrEqual(child, root));
    }

    [Fact]
    public void IsWithinOrEqual_returns_false_for_sibling_path_with_same_prefix()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.RootPath, "workspace");
        var sibling = Path.Combine(workspace.RootPath, "workspace-private", "secret.txt");

        Assert.False(FileSystemPathComparer.IsWithinOrEqual(sibling, root));
    }
}
