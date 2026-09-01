using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class ScriptedGovernedLoopGraphRevisionStore : IGovernedLoopGraphRevisionStore
{
    internal int ReadArtifactCallCount { get; private set; }

    internal Func<GovernedLoopRevisionReference, CancellationToken, Task<GovernedLoopGraphRevisionArtifactReadResult>> ReadArtifactBehavior { get; set; }
        = (_, _) => Task.FromResult(new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, 1, null));

    public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
        GovernedLoopRevisionReference revision,
        CancellationToken cancellationToken = default)
    {
        ReadArtifactCallCount++;
        return ReadArtifactBehavior(revision, cancellationToken);
    }

    public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
