using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

public sealed class GovernedLoopRevisionContractHashTests
{
    [Fact]
    public void Canonical_hashes_are_deterministic_lowercase_domain_separated_and_field_complete()
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var artifactCopy = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var changedActor = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, "create-1", "actor-2", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var pin = GovernedLoopRevisionTestFixture.Pin(revision);
        var head = GovernedLoopRevisionTestFixture.PublishedHead(pin);

        var artifactHash = GovernedLoopRevisionContractHash.ComputeArtifactHash(artifact);

        Assert.Equal(artifactHash, GovernedLoopRevisionContractHash.ComputeArtifactHash(artifactCopy));
        Assert.NotEqual(artifactHash, GovernedLoopRevisionContractHash.ComputeArtifactHash(changedActor));
        Assert.NotEqual(artifactHash, GovernedLoopRevisionContractHash.ComputePublicationPinHash(pin));
        Assert.NotEqual(artifactHash, GovernedLoopRevisionContractHash.ComputeLifecycleHeadHash(head));
        Assert.Equal(64, artifactHash.Length);
        Assert.All(artifactHash, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void Artifact_hash_binds_exact_rollback_publication_provenance()
    {
        var historicalA = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-a");
        var historicalB = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-b");
        var predecessor = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var successor = GovernedLoopRevisionTestFixture.Revision(3);
        var artifactA = GovernedLoopRevisionArtifactFactory.Create(1, successor, predecessor, historicalA, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var artifactB = GovernedLoopRevisionArtifactFactory.Create(1, successor, predecessor, historicalB, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.NotEqual(GovernedLoopRevisionContractHash.ComputeArtifactHash(artifactA), GovernedLoopRevisionContractHash.ComputeArtifactHash(artifactB));
    }

    [Fact]
    public void Operation_evidence_hash_binds_exact_previous_and_resulting_heads()
    {
        var draft = GovernedLoopRevisionTestFixture.Revision(1);
        var previous = GovernedLoopRevisionTestFixture.DraftHead(draft);
        var pin = GovernedLoopRevisionTestFixture.Pin(draft);
        var result = GovernedLoopRevisionTestFixture.PublishedHead(pin);
        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "publish-1",
            previous,
            result,
            null,
            draft,
            validationHash: GovernedLoopRevisionTestFixture.ValidationHash);
        var staleEvidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            "publish-stale",
            previous,
            previous,
            null,
            draft);

        Assert.NotEqual(GovernedLoopRevisionContractHash.ComputeOperationEvidenceHash(evidence), GovernedLoopRevisionContractHash.ComputeOperationEvidenceHash(staleEvidence));
    }

    [Fact]
    public void Hashing_revalidates_public_contracts_before_digesting()
    {
        var invalidPin = new GovernedLoopRevisionPublicationPin(2, GovernedLoopRevisionTestFixture.Revision(1), "publish-1", "bad");
        var invalidHead = new GovernedLoopRevisionLifecycleHead(1, "graph", 1, GovernedLoopRevisionLifecycleStatus.Archived, GovernedLoopRevisionTestFixture.Revision(2, 'e'), GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1)), "archive-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionContractHash.ComputePublicationPinHash(invalidPin));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionContractHash.ComputeLifecycleHeadHash(invalidHead));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopRevisionContractHash.ComputeArtifactHash(null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopRevisionContractHash.ComputeOperationEvidenceHash(null!));
    }
}
