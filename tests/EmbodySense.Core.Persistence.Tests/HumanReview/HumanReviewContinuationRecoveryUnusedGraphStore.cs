using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryUnusedGraphStore(
    GovernedLoopGraphRevisionArtifact? artifact = null,
    GovernedLoopRevisionStoreReadStatus? status = null,
    Exception? exception = null,
    Action? onRead = null) : IGovernedLoopGraphRevisionStore
{
    public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default)
    {
        onRead?.Invoke();
        return exception is null
            ? Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(status ?? (artifact is null ? GovernedLoopRevisionStoreReadStatus.Unavailable : GovernedLoopRevisionStoreReadStatus.Ready), 1, artifact))
            : Task.FromException<GovernedLoopGraphRevisionArtifactReadResult>(exception);
    }

    public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(string graphId, string operationId, string lifecycleRequestHash, string authoringRequestHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(GovernedLoopGraphRevisionStoreMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
