using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

/// <summary>Atomically persists immutable graph payloads with their generic revision lifecycle evidence.</summary>
public interface IGovernedLoopGraphRevisionStore
{
    /// <summary>Reads one exact graph aggregate without selecting a revision for the caller.</summary>
    Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one exact immutable graph revision.</summary>
    Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
        GovernedLoopRevisionReference revision,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one graph aggregate and the workspace-global full authoring intent bound to an operation.</summary>
    Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically commits lifecycle evidence and its optional canonical graph payload.</summary>
    Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default);
}
