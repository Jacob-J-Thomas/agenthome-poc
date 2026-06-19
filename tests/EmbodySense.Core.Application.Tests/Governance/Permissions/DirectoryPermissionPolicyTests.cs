using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Governance.Permissions;

public sealed class DirectoryPermissionPolicyTests
{
    [Fact]
    public void EvaluateDirectory_returns_requires_approval_when_policy_is_missing()
    {
        using var workspace = new TestWorkspace();
        var policy = DirectoryPermissionPolicy.Create(new WorkspacePaths(workspace.RootPath), null);

        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "shared"), FileSystemOperation.Read);

        Assert.Equal(PermissionDecision.RequiresApproval, evaluation.Decision);
        Assert.Contains("permissions.json", evaluation.Detail);
    }

    [Fact]
    public void EvaluateDirectory_prefers_more_specific_denied_rule()
    {
        using var workspace = new TestWorkspace();
        var policy = DirectoryPermissionPolicy.Create(new WorkspacePaths(workspace.RootPath), new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission { Path = "workspace", Operations = [FileSystemOperation.Read], RequiresApproval = false }
            ],
            Denied =
            [
                new DeniedFileSystemPermission { Path = "workspace/private", Operations = [FileSystemOperation.Read] }
            ]
        });
        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "private"), FileSystemOperation.Read);

        Assert.Equal(PermissionDecision.Deny, evaluation.Decision);
        Assert.Equal("workspace/private", evaluation.MatchedPath);
    }

    [Fact]
    public void EvaluateDirectory_returns_requires_approval_for_approved_rule_marked_requires_approval()
    {
        using var workspace = new TestWorkspace();
        var policy = DirectoryPermissionPolicy.Create(new WorkspacePaths(workspace.RootPath), new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission { Path = "workspace/generated", Operations = [FileSystemOperation.Modify], RequiresApproval = true }
            ]
        });
        var evaluation = policy.EvaluateDirectory(workspace.File("workspace", "generated"), FileSystemOperation.Modify);

        Assert.Equal(PermissionDecision.RequiresApproval, evaluation.Decision);
        Assert.Equal("workspace/generated", evaluation.MatchedPath);
    }
}
