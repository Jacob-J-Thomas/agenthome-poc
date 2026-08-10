using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

public sealed class GovernedLoopRevisionContractTests
{
    [Fact]
    public void Factories_create_exact_schema_one_artifact_pin_and_head_contracts()
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(1, revision, null, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var pin = GovernedLoopRevisionTestFixture.Pin(revision);
        var head = GovernedLoopRevisionTestFixture.PublishedHead(pin);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(artifact).IsValid);
        Assert.True(GovernedLoopRevisionContractValidator.Validate(pin).IsValid);
        Assert.True(GovernedLoopRevisionContractValidator.Validate(head).IsValid);
        Assert.Equal(GovernedLoopRevisionContractLimits.CurrentSchemaVersion, artifact.SchemaVersion);
        Assert.Equal(revision, pin.Revision);
        Assert.Equal(pin, head.PublishedRevision);
        Assert.Null(artifact.PredecessorRevision);
        Assert.Null(artifact.RollbackSourcePublication);
    }

    [Fact]
    public void Rollback_artifact_retains_exact_historical_publication_and_distinct_successor_lineage()
    {
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-historical");
        var current = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var successor = GovernedLoopRevisionTestFixture.Revision(3);

        var artifact = GovernedLoopRevisionArtifactFactory.Create(1, successor, current, historical, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Equal(current, artifact.PredecessorRevision);
        Assert.Equal(historical, artifact.RollbackSourcePublication);
        Assert.NotEqual(historical.Revision, artifact.Revision);
        Assert.Equal(historical, artifact.RollbackSourcePublication);
    }

    [Fact]
    public void Artifact_validator_rejects_self_lineage_missing_rollback_predecessor_and_cross_graph_substitution()
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var otherGraph = GovernedLoopRevisionTestFixture.Revision(2, 'e', "other-graph");
        var historical = GovernedLoopRevisionTestFixture.Pin(otherGraph, "publish-other");
        var self = new GovernedLoopRevisionArtifact(1, revision, revision, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var missingPredecessor = new GovernedLoopRevisionArtifact(1, revision, null, GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(2, 'e')), "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var substituted = new GovernedLoopRevisionArtifact(1, revision, otherGraph, historical, "rollback-2", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(self).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage);
        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(missingPredecessor).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage);
        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(substituted).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.GraphMismatch);
    }

    [Fact]
    public void Rollback_artifact_rejects_successor_with_different_executable_content()
    {
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1));
        var predecessor = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var forgedSuccessor = GovernedLoopRevisionTestFixture.Revision(3, 'f');

        var validation = GovernedLoopRevisionContractValidator.Validate(new GovernedLoopRevisionArtifact(1, forgedSuccessor, predecessor, historical, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage && error.Path.EndsWith("executableHash", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionArtifactFactory.Create(1, forgedSuccessor, predecessor, historical, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
    }

    [Fact]
    public void Stable_revision_identifier_cannot_be_rebound_to_different_executable_content()
    {
        var original = GovernedLoopRevisionTestFixture.Revision(1);
        var changedContent = GovernedLoopRevisionTestFixture.Revision(1, 'e');
        var artifact = new GovernedLoopRevisionArtifact(1, changedContent, original, null, "replace-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var conflictingHead = new GovernedLoopRevisionLifecycleHead(
            1,
            "graph",
            2,
            GovernedLoopRevisionLifecycleStatus.Published,
            original,
            GovernedLoopRevisionTestFixture.Pin(changedContent),
            "replace-1",
            GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var current = GovernedLoopRevisionTestFixture.DraftHead(original);
        var rewritten = GovernedLoopRevisionTestFixture.DraftHead(changedContent, 2, "replace-1", current.UpdatedAtUtc.AddMinutes(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(artifact).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage);
        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(conflictingHead).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition);
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(current, rewritten).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage);
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionArtifactFactory.Create(1, changedContent, original, null, "replace-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
    }

    [Fact]
    public void Malformed_nested_revision_dtos_fail_with_bounded_validation_instead_of_null_dereferences()
    {
        var hash = GovernedLoopRevisionTestFixture.ValidationHash;
        var current = GovernedLoopRevisionTestFixture.Revision(1);
        var candidate = GovernedLoopRevisionTestFixture.Revision(2);
        var badPin = new GovernedLoopRevisionPublicationPin(1, null!, "publish-bad", hash);
        var validSource = GovernedLoopRevisionTestFixture.Pin(current, "publish-source");
        var nullArtifactRevision = new GovernedLoopRevisionArtifact(1, null!, current, validSource, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var nullRollbackRevision = new GovernedLoopRevisionArtifact(1, candidate, current, badPin, "rollback-2", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var badHead = new GovernedLoopRevisionLifecycleHead(1, "graph", 2, GovernedLoopRevisionLifecycleStatus.Published, null, badPin, "publish-bad", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var sourceOnlyEvidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "rollback-3",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.NotFound,
            GovernedLoopRevisionOperationFailureCode.PublicationNotFound,
            null,
            null,
            null,
            null,
            badPin,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(GovernedLoopRevisionTestFixture.Pin(current, "publish-current"));
        var rollbackResult = GovernedLoopRevisionTestFixture.PublishedHead(
            GovernedLoopRevisionTestFixture.Pin(candidate, "rollback-bad"),
            version: previous.LifecycleVersion + 1,
            operationId: "rollback-bad",
            updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));
        var committedEvidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "rollback-bad",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            rollbackResult,
            candidate,
            current,
            badPin,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            GovernedLoopRevisionTestFixture.ValidationHash,
            rollbackResult.UpdatedAtUtc.AddSeconds(1));
        var nestedHeadEvidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "disable-bad",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Disable,
            GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            badHead,
            badHead,
            null,
            current,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.False(GovernedLoopRevisionContractValidator.Validate(nullArtifactRevision).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(nullRollbackRevision).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(badHead).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(sourceOnlyEvidence).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(committedEvidence).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(nestedHeadEvidence).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionContractHash.ComputeArtifactHash(nullRollbackRevision));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionContractHash.ComputeOperationEvidenceHash(sourceOnlyEvidence));
    }

    [Fact]
    public void Rollback_artifact_source_must_use_a_historical_operation_distinct_from_successor_creation()
    {
        var source = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "rollback-1");
        var predecessor = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var candidate = GovernedLoopRevisionTestFixture.Revision(3);
        var artifact = new GovernedLoopRevisionArtifact(1, candidate, predecessor, source, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(artifact).Errors, error => error.Path.EndsWith("publicationOperationId", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionArtifactFactory.Create(1, candidate, predecessor, source, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
    }

    [Theory]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Draft, true, false, true)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Draft, false, false, false)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Published, false, true, true)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Published, true, true, true)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Disabled, true, true, true)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Archived, false, true, true)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Archived, true, true, false)]
    [InlineData(GovernedLoopRevisionLifecycleStatus.Unknown, true, false, false)]
    public void Lifecycle_status_has_closed_exact_head_composition(
        GovernedLoopRevisionLifecycleStatus status,
        bool hasDraft,
        bool hasPublication,
        bool expected)
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var draft = hasDraft ? GovernedLoopRevisionTestFixture.Revision(2, 'e') : null;
        var publication = hasPublication ? GovernedLoopRevisionTestFixture.Pin(revision) : null;
        var head = new GovernedLoopRevisionLifecycleHead(1, "graph", 1, status, draft, publication, "operation-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Equal(expected, GovernedLoopRevisionContractValidator.Validate(head).IsValid);
    }

    [Fact]
    public void Lifecycle_head_rejects_cross_graph_heads_same_draft_and_publication_and_non_utc_time()
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var pin = GovernedLoopRevisionTestFixture.Pin(revision);
        var crossGraph = new GovernedLoopRevisionLifecycleHead(1, "other-graph", 1, GovernedLoopRevisionLifecycleStatus.Published, null, pin, "publish-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var sameHeads = new GovernedLoopRevisionLifecycleHead(1, "graph", 1, GovernedLoopRevisionLifecycleStatus.Published, revision, pin, "publish-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);
        var nonUtc = new GovernedLoopRevisionLifecycleHead(1, "graph", 1, GovernedLoopRevisionLifecycleStatus.Published, null, pin, "publish-1", GovernedLoopRevisionTestFixture.CreatedAtUtc.ToOffset(TimeSpan.FromHours(1)));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(crossGraph).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.GraphMismatch);
        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(sameHeads).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition);
        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(nonUtc).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidTimestamp);
    }

    [Fact]
    public void Validators_reject_malformed_schema_identifiers_hashes_versions_and_enums_without_echoing_values()
    {
        const string SecretCanary = "SECRET-canary-value";
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var pin = new GovernedLoopRevisionPublicationPin(2, revision, SecretCanary, SecretCanary);
        var head = new GovernedLoopRevisionLifecycleHead(2, SecretCanary, GovernedLoopRevisionContractLimits.MaxLifecycleVersion + 1, (GovernedLoopRevisionLifecycleStatus)999, null, pin, SecretCanary, default);

        var validation = GovernedLoopRevisionContractValidator.Validate(head);

        Assert.False(validation.IsValid);
        Assert.True(validation.Errors.Count <= GovernedLoopRevisionContractLimits.MaxValidationErrors);
        Assert.DoesNotContain(validation.Errors, error => error.Message.Contains(SecretCanary, StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.UnsupportedSchemaVersion);
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidIdentifier);
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLifecycleVersion);
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidEnumeration);
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHash);
    }

    [Fact]
    public void Factories_reject_invalid_schema_lineage_hash_and_nested_contracts()
    {
        var revision = GovernedLoopRevisionTestFixture.Revision(1);
        var invalidPin = new GovernedLoopRevisionPublicationPin(1, null!, "publish-1", GovernedLoopRevisionTestFixture.ValidationHash);

        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionArtifactFactory.Create(2, revision, null, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionArtifactFactory.Create(1, revision, revision, null, "create-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "publish-1", "BAD"));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopRevisionLifecycleHeadFactory.Create(1, "graph", 1, GovernedLoopRevisionLifecycleStatus.Published, null, invalidPin, "publish-1", GovernedLoopRevisionTestFixture.CreatedAtUtc));
    }

    [Fact]
    public void Public_contract_limits_are_finite_and_ordered_for_hostile_store_preflight()
    {
        Assert.InRange(GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph, 1, GovernedLoopRevisionContractLimits.MaxOperationsPerGraph);
        Assert.InRange(GovernedLoopRevisionContractLimits.MaxOperationsPerGraph, GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph, 100_000);
        Assert.InRange(GovernedLoopRevisionContractLimits.MaxGraphsPerStore, 1, 100_000);
        Assert.InRange(GovernedLoopRevisionContractLimits.MaxValidationErrors, 1, 128);
    }

    [Fact]
    public void Validators_return_bounded_required_errors_for_absent_public_contracts()
    {
        var artifact = GovernedLoopRevisionContractValidator.Validate((GovernedLoopRevisionArtifact?)null);
        var pin = GovernedLoopRevisionContractValidator.Validate((GovernedLoopRevisionPublicationPin?)null);
        var head = GovernedLoopRevisionContractValidator.Validate((GovernedLoopRevisionLifecycleHead?)null);
        var evidence = GovernedLoopRevisionContractValidator.Validate((GovernedLoopRevisionOperationEvidence?)null);
        var transition = GovernedLoopRevisionContractValidator.ValidateTransition(null, null);

        Assert.All(new[] { artifact, pin, head, evidence, transition }, result =>
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal(GovernedLoopRevisionValidationErrorCode.ContractRequired, error.Code);
            Assert.Equal("$", error.Path);
        });
    }

    [Fact]
    public void Rollback_artifact_rejects_a_successor_that_reuses_the_source_revision_identity()
    {
        var sourceRevision = GovernedLoopRevisionTestFixture.Revision(1);
        var source = GovernedLoopRevisionTestFixture.Pin(sourceRevision, "publish-source");
        var predecessor = GovernedLoopRevisionTestFixture.Revision(2);
        var artifact = new GovernedLoopRevisionArtifact(1, sourceRevision, predecessor, source, "rollback-1", "actor-1", GovernedLoopRevisionTestFixture.CreatedAtUtc);

        var result = GovernedLoopRevisionContractValidator.Validate(artifact);

        Assert.Contains(result.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage && error.Path.EndsWith("revision", StringComparison.Ordinal));
    }

}
