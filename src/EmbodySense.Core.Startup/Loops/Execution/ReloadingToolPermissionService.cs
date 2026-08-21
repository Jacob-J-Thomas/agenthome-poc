using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
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

internal sealed class ReloadingToolPermissionService : IToolPermissionService
{
    private readonly WorkspacePaths _paths;
    private readonly IPermissionPolicyStore _policyStore;

    /// <summary>
    /// Creates an evaluator that reloads the workspace permission document for every request.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="policyStore">The policy store.</param>
    public ReloadingToolPermissionService(WorkspacePaths paths, IPermissionPolicyStore policyStore)
    {
        _paths = paths;
        _policyStore = policyStore;
    }

    /// <summary>
    /// Evaluates one request against a freshly loaded, fail-closed directory policy.
    /// </summary>
    /// <param name="request">The governed workspace request.</param>
    /// <returns>The permission decision produced from the policy observed at evaluation time.</returns>
    public ToolPermissionCheck Evaluate(ToolRequest request)
    {
        return new ToolPermissionService(_paths, _policyStore.Load(_paths)).Evaluate(request);
    }

    /// <inheritdoc />
    public ToolPermissionCheck EvaluateExactFileMutation(ToolRequest request, FileSystemOperation operation)
    {
        return new ToolPermissionService(_paths, _policyStore.Load(_paths)).EvaluateExactFileMutation(request, operation);
    }
}
