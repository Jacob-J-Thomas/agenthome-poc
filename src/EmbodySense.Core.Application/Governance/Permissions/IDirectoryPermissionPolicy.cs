using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.Governance.Permissions;

public interface IDirectoryPermissionPolicy
{
    bool HasDocument { get; }

    IReadOnlyList<ApprovedFileSystemPermission> Approved { get; }

    IReadOnlyList<DeniedFileSystemPermission> Denied { get; }

    PermissionEvaluation EvaluateDirectory(string directoryPath, FileSystemOperation operation);

    bool CanReadDirectory(string directoryPath);

    bool CanAppendDirectory(string directoryPath);

    bool CanModifyDirectory(string directoryPath);
}
