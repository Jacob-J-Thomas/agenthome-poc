using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops.Revisions;

internal static class GovernedLoopRevisionLifecycleStoreTestData
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset _recordedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopRevisionStoreMutation CreateDraftMutation(
        string graphId,
        string revisionId,
        string operationId,
        string requestHash,
        long generation)
    {
        var revision = GovernedLoopRevisionReference.Create(1, graphId, revisionId, HashA);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            revision,
            null,
            operationId,
            _recordedAtUtc);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, operationId, "actor-one", _recordedAtUtc);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            requestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            head,
            revision,
            null,
            null,
            HashB,
            null,
            _recordedAtUtc);
        return new GovernedLoopRevisionStoreMutation(graphId, generation, operation, artifact, head);
    }
}
