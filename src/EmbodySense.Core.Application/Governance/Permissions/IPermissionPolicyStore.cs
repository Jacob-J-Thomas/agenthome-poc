using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Governance.Permissions;

public interface IPermissionPolicyStore
{
    IDirectoryPermissionPolicy Load(WorkspacePaths paths);

    string CreateDefaultJson(WorkspacePaths paths);
}
