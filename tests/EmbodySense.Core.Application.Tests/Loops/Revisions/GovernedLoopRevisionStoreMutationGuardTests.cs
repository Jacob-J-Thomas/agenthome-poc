using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Revisions;

public sealed class GovernedLoopRevisionStoreMutationGuardTests
{
    private static readonly DateTimeOffset _time = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Complete_committed_mutation_requires_exact_head_artifact_and_operation_provenance()
    {
        var valid = CreateMutation();

        Assert.True(GovernedLoopRevisionStoreMutationGuard.IsValid(valid));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(null));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(valid with { ExpectedStoreGeneration = -1 }));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(valid with { GraphId = "other-graph" }));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(valid with { HeadToWrite = null }));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(valid with
        {
            HeadToWrite = valid.HeadToWrite! with { LifecycleVersion = 2 },
        }));
        Assert.False(GovernedLoopRevisionStoreMutationGuard.IsValid(valid with
        {
            ArtifactToAppend = valid.ArtifactToAppend! with { CreatedByActorId = "actor-two" },
        }));
    }

    [Fact]
    public void Conclusive_noncommit_does_not_require_a_head_or_artifact()
    {
        var committed = CreateMutation();
        var failure = committed.Operation with
        {
            Kind = GovernedLoopRevisionOperationKind.ReplaceDraft,
            Outcome = GovernedLoopRevisionOperationOutcome.NotFound,
            FailureCode = GovernedLoopRevisionOperationFailureCode.LifecycleNotFound,
            ResultHead = null,
            TargetRevision = Revision("missing-revision"),
        };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(failure).IsValid);

        Assert.True(GovernedLoopRevisionStoreMutationGuard.IsValid(new GovernedLoopRevisionStoreMutation(
            committed.GraphId,
            committed.ExpectedStoreGeneration,
            failure,
            null,
            null)));
    }

    private static GovernedLoopRevisionStoreMutation CreateMutation()
    {
        var revision = Revision("revision-one");
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            revision.GraphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            revision,
            null,
            "create-one",
            _time);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            revision,
            null,
            null,
            "create-one",
            "actor-one",
            _time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            "create-one",
            "actor-one",
            Hash('a'),
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            head,
            revision,
            null,
            null,
            Hash('b'),
            null,
            _time);
        return new GovernedLoopRevisionStoreMutation(revision.GraphId, 0, operation, artifact, head);
    }

    private static GovernedLoopRevisionReference Revision(string revisionId)
        => GovernedLoopRevisionReference.Create(1, "graph-one", revisionId, Hash('c'));

    private static string Hash(char value) => new(value, 64);
}
