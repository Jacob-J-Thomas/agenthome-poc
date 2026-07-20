using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class AdmittedMaximumAuthorityProvider : ICustomLoopToolAuthorityProvider
{
    private static readonly CustomLoopToolAssignment[] Catalog = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search];

    public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
    {
        var admitted = admittedMaximum.ToArray();
        return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
            roleId,
            admitted,
            admitted,
            Catalog,
            admitted,
            CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash(roleId, admitted),
            CustomLoopToolAuthorityProvider.ComputeCatalogHash(),
            DateTimeOffset.UtcNow,
            true,
            "Test-only authority treats the immutable admitted maximum as the current role ceiling."));
    }
}
