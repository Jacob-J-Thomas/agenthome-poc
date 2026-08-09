using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.LocalWorkspace;

public sealed class CapabilityAuthorityWorkspaceMutationCommitBoundaryTests
{
    [Fact]
    public async Task ExecuteAsync_fences_skill_descendants_after_normalizing_path_aliases()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);
        var aliasedSkillPath = Path.Combine(paths.AgentPath, "skills", "nested", "..", "manifest.json");

        var result = await boundary.ExecuteAsync([aliasedSkillPath], _ => Task.FromResult("committed"));

        Assert.Equal("committed", result);
        Assert.Equal(1, authority.Executions);
    }

    [Theory]
    [InlineData(".agent/SKILLS/manifest.json")]
    [InlineData(".AGENT/skills")]
    [InlineData(".AGENT")]
    public async Task ExecuteAsync_fences_case_aliased_skill_trees_on_case_insensitive_hosts(string relativePath)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        await boundary.ExecuteAsync([Path.Combine(paths.RootPath, relativePath)], _ => Task.FromResult(true));

        var expectedExecutions = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? 1 : 0;
        Assert.Equal(expectedExecutions, authority.Executions);
    }

    [Fact]
    public async Task ExecuteAsync_fences_canonically_equivalent_skill_trees_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var canonicalWorkspaceRoot = Path.Combine(workspace.RootPath, "caf\u00e9");
        var paths = new WorkspacePaths(canonicalWorkspaceRoot);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);
        var aliasedWorkspaceRoot = Path.Combine(workspace.RootPath, "cafe\u0301");

        await boundary.ExecuteAsync([Path.Combine(aliasedWorkspaceRoot, ".agent", "skills", "manifest.json")], _ => Task.FromResult(true));

        Assert.Equal(1, authority.Executions);
    }

    [Theory]
    [InlineData(".agent/skills")]
    [InlineData(".agent")]
    [InlineData(".")]
    public async Task ExecuteAsync_fences_skill_root_and_ancestor_tree_commits(string relativePath)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        await boundary.ExecuteAsync([Path.Combine(paths.RootPath, relativePath)], _ => Task.FromResult(true));

        Assert.Equal(1, authority.Executions);
    }

    [Fact]
    public async Task ExecuteAsync_fences_a_multi_path_commit_when_either_tree_overlaps_skills()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        await boundary.ExecuteAsync([Path.Combine(paths.RootPath, "shared", "source.json"), Path.Combine(paths.SkillsPath, "destination.json")], _ => Task.FromResult(true));

        Assert.Equal(1, authority.Executions);
    }

    [Theory]
    [InlineData("skills-generated")]
    [InlineData("SKILLS-generated")]
    public async Task ExecuteAsync_bypasses_capability_authority_for_non_skill_sibling_commits(string siblingDirectory)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        var result = await boundary.ExecuteAsync([Path.Combine(paths.AgentPath, siblingDirectory, "note.txt")], _ => Task.FromResult("committed"));

        Assert.Equal("committed", result);
        Assert.Equal(0, authority.Executions);
    }

    [Fact]
    public async Task ExecuteAsync_checks_cancellation_before_non_skill_commit_bypass()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var committed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.ExecuteAsync([Path.Combine(paths.RootPath, "shared", "note.txt")], _ =>
        {
            committed = true;
            return Task.FromResult(true);
        }, cancellation.Token));

        Assert.False(committed);
        Assert.Equal(0, authority.Executions);
    }

    [Fact]
    public async Task Constructor_and_execution_require_explicit_dependencies_and_targets()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new StubCapabilityAuthorityTransaction();
        var boundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, authority);

        Assert.Throws<ArgumentNullException>(() => new CapabilityAuthorityWorkspaceMutationCommitBoundary(null!, authority));
        Assert.Throws<ArgumentNullException>(() => new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => boundary.ExecuteAsync<bool>(null!, _ => Task.FromResult(true)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => boundary.ExecuteAsync<bool>([paths.SkillsPath], null!));
        await Assert.ThrowsAsync<ArgumentException>(() => boundary.ExecuteAsync([], _ => Task.FromResult(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => boundary.ExecuteAsync([""], _ => Task.FromResult(true)));
    }
}
