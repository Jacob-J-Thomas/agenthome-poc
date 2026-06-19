using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Application.Governance.Permissions;

public interface IPermissionPolicyStore
{
    IDirectoryPermissionPolicy Load(WorkspacePaths paths);

    string CreateDefaultJson(WorkspacePaths paths);
}
