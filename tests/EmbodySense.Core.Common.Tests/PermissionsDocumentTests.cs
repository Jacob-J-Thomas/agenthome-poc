using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests;

public sealed class PermissionsDocumentTests
{
    [Fact]
    public void CreateDefault_requires_approval_for_retained_tool_response_inspection()
    {
        using var workspace = new TestWorkspace();

        var document = PermissionsDocument.CreateDefault(new WorkspacePaths(workspace.RootPath));

        var inspectionRules = document.Approved.Where(IsInspectionRule).ToArray();
        var operations = inspectionRules.Where(rule => rule.RequiresApproval).SelectMany(rule => rule.Operations).ToHashSet();
        Assert.Contains(FileSystemOperation.List, operations);
        Assert.Contains(FileSystemOperation.Read, operations);
        Assert.DoesNotContain(inspectionRules, rule => !rule.RequiresApproval && rule.Operations.Any(IsInspectionOperation));
        Assert.False(document.EnsureToolResponseInspectionApproval(new WorkspacePaths(workspace.RootPath)));
    }

    [Fact]
    public void EnsureToolResponseInspectionApproval_splits_nonapproval_coverage_without_broadening_other_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var document = new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission
                {
                    Path = "././.agent/logs/tool-responses",
                    Operations = [FileSystemOperation.List, FileSystemOperation.Read, FileSystemOperation.Modify],
                    RequiresApproval = false
                }
            ]
        };

        var changed = document.EnsureToolResponseInspectionApproval(paths);

        Assert.True(changed);
        var nonApproval = Assert.Single(document.Approved, rule => IsInspectionRule(rule) && !rule.RequiresApproval);
        Assert.Equal([FileSystemOperation.Modify], nonApproval.Operations);
        var approval = Assert.Single(document.Approved, rule => IsInspectionRule(rule) && rule.RequiresApproval);
        Assert.Equal([FileSystemOperation.List, FileSystemOperation.Read], approval.Operations.OrderBy(operation => operation).ToArray());
        Assert.False(document.EnsureToolResponseInspectionApproval(paths));
    }

    [Fact]
    public void EnsureToolResponseInspectionApproval_adds_only_missing_approval_coverage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var document = new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission
                {
                    Path = ".agent/logs/other/../tool-responses/",
                    Operations = [FileSystemOperation.List],
                    RequiresApproval = true
                },
                new ApprovedFileSystemPermission
                {
                    Path = PermissionsDocument.ToolResponseInspectionPath,
                    Operations = [FileSystemOperation.Read],
                    RequiresApproval = false
                }
            ]
        };

        Assert.True(document.EnsureToolResponseInspectionApproval(paths));

        Assert.DoesNotContain(document.Approved, rule => IsInspectionRule(rule) && !rule.RequiresApproval && rule.Operations.Any(IsInspectionOperation));
        var approvalOperations = document.Approved.Where(rule => IsInspectionRule(rule) && rule.RequiresApproval).SelectMany(rule => rule.Operations).ToArray();
        Assert.Equal(1, approvalOperations.Count(operation => operation == FileSystemOperation.List));
        Assert.Equal(1, approvalOperations.Count(operation => operation == FileSystemOperation.Read));
    }

    [Fact]
    public void Json_round_trip_preserves_approval_only_inspection_coverage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var document = new PermissionsDocument
        {
            Approved =
            [
                new ApprovedFileSystemPermission
                {
                    Path = PermissionsDocument.ToolResponseInspectionPath,
                    Operations = [FileSystemOperation.List, FileSystemOperation.Read],
                    RequiresApproval = false
                }
            ]
        };
        Assert.True(document.EnsureToolResponseInspectionApproval(paths));

        var restored = Assert.IsType<PermissionsDocument>(PermissionsDocument.FromJson(document.ToJson()));

        Assert.DoesNotContain(restored.Approved, rule => IsInspectionRule(rule) && !rule.RequiresApproval && rule.Operations.Any(IsInspectionOperation));
        var approvalOperations = restored.Approved.Where(rule => IsInspectionRule(rule) && rule.RequiresApproval).SelectMany(rule => rule.Operations).ToHashSet();
        Assert.Contains(FileSystemOperation.List, approvalOperations);
        Assert.Contains(FileSystemOperation.Read, approvalOperations);
    }

    private static bool IsInspectionRule(ApprovedFileSystemPermission rule)
    {
        return string.Equals(rule.Path.Replace('\\', '/').Trim('/').TrimStart('.', '/'), PermissionsDocument.ToolResponseInspectionPath.TrimStart('.', '/'), StringComparison.Ordinal);
    }

    private static bool IsInspectionOperation(FileSystemOperation operation)
    {
        return operation is FileSystemOperation.List or FileSystemOperation.Read;
    }
}
