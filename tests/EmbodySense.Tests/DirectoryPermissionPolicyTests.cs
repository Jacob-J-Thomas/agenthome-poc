using System.Text.Json;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Permissions.Models;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

public sealed class DirectoryPermissionPolicyTests
{
    [Fact]
    public void EvaluateDirectory_returns_requires_approval_when_policy_is_missing()
    {
        using var workspace = new TestWorkspace();
        var policy = DirectoryPermissionPolicy.Load(new WorkspacePaths(workspace.RootPath));

        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "shared"), FileSystemOperation.Read);

        Assert.Equal(PermissionDecision.RequiresApproval, evaluation.Decision);
        Assert.Contains("permissions.json", evaluation.Detail);
    }

    [Fact]
    public void EvaluateDirectory_prefers_more_specific_denied_rule()
    {
        using var workspace = new TestWorkspace();
        WritePermissions(workspace, new
        {
            version = 2,
            scope = "single-file-system-directory-level",
            approved = new[]
            {
                new { path = "workspace", operations = new[] { "read" }, requiresApproval = false }
            },
            denied = new[]
            {
                new { path = "workspace/private", operations = new[] { "read" } }
            }
        });

        var policy = DirectoryPermissionPolicy.Load(new WorkspacePaths(workspace.RootPath));
        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "private"), FileSystemOperation.Read);

        Assert.Equal(PermissionDecision.Deny, evaluation.Decision);
        Assert.Equal("workspace/private", evaluation.MatchedPath);
    }

    [Fact]
    public void EvaluateDirectory_returns_requires_approval_for_approved_rule_marked_requires_approval()
    {
        using var workspace = new TestWorkspace();
        WritePermissions(workspace, new
        {
            version = 2,
            scope = "single-file-system-directory-level",
            approved = new[]
            {
                new { path = "workspace/generated", operations = new[] { "modify" }, requiresApproval = true }
            },
            denied = Array.Empty<object>()
        });

        var policy = DirectoryPermissionPolicy.Load(new WorkspacePaths(workspace.RootPath));
        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "generated"), FileSystemOperation.Modify);

        Assert.Equal(PermissionDecision.RequiresApproval, evaluation.Decision);
        Assert.Equal("workspace/generated", evaluation.MatchedPath);
    }

    private static void WritePermissions(TestWorkspace workspace, object document)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        File.WriteAllText(paths.PermissionsPath, JsonSerializer.Serialize(document));
    }
}
