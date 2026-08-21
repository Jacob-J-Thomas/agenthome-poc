using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Reads exact parent and target completion posture without mutating lifecycle state.</summary>
public interface IAuthorityDelegationCompletionSource
{
    /// <summary>Resolves completion for one exact workspace, parent execution, and target.</summary>
    Task<AuthorityDelegationCompletionResolution> ResolveAsync(
        string workspaceId,
        GovernedLoopExecutionBinding parentExecution,
        AuthorityDelegationTargetBinding target,
        CancellationToken cancellationToken = default);
}
