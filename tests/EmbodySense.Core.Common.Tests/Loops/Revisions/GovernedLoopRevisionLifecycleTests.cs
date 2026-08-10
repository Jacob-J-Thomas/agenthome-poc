using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

public sealed class GovernedLoopRevisionLifecycleTests
{
    [Fact]
    public void First_draft_creation_commits_exact_candidate_at_version_one()
    {
        var candidate = GovernedLoopRevisionTestFixture.Revision(1);
        var result = GovernedLoopRevisionTestFixture.DraftHead(candidate);

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "create-1",
            null,
            result,
            candidate,
            null);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(candidate, evidence.ResultHead!.DraftRevision);
        Assert.Null(evidence.PreviousHead);
    }

    [Fact]
    public void Draft_replacement_targets_exact_current_draft_and_preserves_draft_posture()
    {
        var current = GovernedLoopRevisionTestFixture.Revision(1);
        var candidate = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var previous = GovernedLoopRevisionTestFixture.DraftHead(current);
        var result = GovernedLoopRevisionTestFixture.DraftHead(candidate, 2, "replace-1", previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "replace-1",
            previous,
            result,
            candidate,
            current);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Draft, evidence.ResultHead!.Status);
    }

    [Fact]
    public void Draft_replacement_cannot_rebind_the_current_revision_identifier_to_new_content()
    {
        var current = GovernedLoopRevisionTestFixture.Revision(1);
        var changedContent = GovernedLoopRevisionTestFixture.Revision(1, 'e');
        var previous = GovernedLoopRevisionTestFixture.DraftHead(current);
        var result = GovernedLoopRevisionTestFixture.DraftHead(changedContent, 2, "replace-1", previous.UpdatedAtUtc.AddMinutes(1));
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "replace-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            result,
            changedContent,
            current,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            result.UpdatedAtUtc.AddSeconds(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(evidence).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidLineage);
    }

    [Fact]
    public void Successor_draft_can_start_from_publication_without_changing_exact_publication_pin()
    {
        var published = GovernedLoopRevisionTestFixture.Revision(1);
        var candidate = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var pin = GovernedLoopRevisionTestFixture.Pin(published);
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(pin);
        var result = GovernedLoopRevisionTestFixture.PublishedHead(pin, candidate, 3, "replace-1", previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "replace-1",
            previous,
            result,
            candidate,
            published);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(pin, evidence.ResultHead!.PublishedRevision);
        Assert.Equal(candidate, evidence.ResultHead.DraftRevision);
    }

    [Fact]
    public void Publication_pins_exact_current_draft_and_clears_draft_head()
    {
        var draft = GovernedLoopRevisionTestFixture.Revision(1);
        var previous = GovernedLoopRevisionTestFixture.DraftHead(draft);
        var pin = GovernedLoopRevisionTestFixture.Pin(draft);
        var result = GovernedLoopRevisionTestFixture.PublishedHead(pin, version: 2, updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));

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

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Null(evidence.ResultHead!.DraftRevision);
        Assert.Equal(draft, evidence.ResultHead.PublishedRevision!.Revision);
    }

    [Fact]
    public void Disablement_changes_only_posture_and_retains_draft_and_exact_publication()
    {
        var publication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1));
        var draft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(publication, draft, 3, "replace-1");
        var result = GovernedLoopRevisionTestFixture.DisabledHead(publication, draft, 4, "disable-1", previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Disable,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "disable-1",
            previous,
            result,
            null,
            publication.Revision);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(publication, evidence.ResultHead!.PublishedRevision);
        Assert.Equal(draft, evidence.ResultHead.DraftRevision);
    }

    [Fact]
    public void Archival_is_terminal_retains_publication_and_clears_only_the_draft_head()
    {
        var publication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1));
        var draft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var previous = GovernedLoopRevisionTestFixture.DisabledHead(publication, draft, 4, "disable-1");
        var result = GovernedLoopRevisionLifecycleHeadFactory.Create(1, "graph", 5, GovernedLoopRevisionLifecycleStatus.Archived, null, publication, "archive-1", previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Archive,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "archive-1",
            previous,
            result,
            null,
            publication.Revision);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.ValidateTransition(result, result with { LifecycleVersion = 6, LastOperationId = "later-1" }).IsValid);
    }

    [Fact]
    public void Rollback_creates_and_publishes_distinct_successor_with_exact_historical_provenance()
    {
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-historical");
        var current = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(2, 'e'), "publish-current");
        var candidate = GovernedLoopRevisionTestFixture.Revision(3);
        var currentDraft = GovernedLoopRevisionTestFixture.Revision(4, 'a');
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, currentDraft, 4, "publish-current");
        var successorPin = GovernedLoopRevisionTestFixture.Pin(candidate, "rollback-1");
        var result = GovernedLoopRevisionTestFixture.PublishedHead(successorPin, version: 5, operationId: "rollback-1", updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "rollback-1",
            previous,
            result,
            candidate,
            currentDraft,
            historical,
            GovernedLoopRevisionTestFixture.ValidationHash);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(historical, evidence.RollbackSourcePublication);
        Assert.Equal(candidate, evidence.ResultHead!.PublishedRevision!.Revision);
        Assert.NotEqual(historical.Revision, candidate);
    }

    [Fact]
    public void Committed_rollback_rejects_candidate_with_content_different_from_historical_publication()
    {
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-historical");
        var current = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(2, 'e'), "publish-current");
        var forgedCandidate = GovernedLoopRevisionTestFixture.Revision(3, 'f');
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, version: 4, operationId: "publish-current");
        var forgedPin = GovernedLoopRevisionTestFixture.Pin(forgedCandidate, "rollback-1");
        var result = GovernedLoopRevisionTestFixture.PublishedHead(forgedPin, version: 5, operationId: "rollback-1", updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "rollback-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            result,
            forgedCandidate,
            current.Revision,
            historical,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            GovernedLoopRevisionTestFixture.ValidationHash,
            result.UpdatedAtUtc.AddSeconds(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(evidence).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition);
    }

    [Fact]
    public void Committed_operation_kind_cannot_claim_an_unrelated_head_transition()
    {
        var publication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1));
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(publication);
        var changedDraft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var invalidResult = GovernedLoopRevisionTestFixture.DisabledHead(publication, changedDraft, 3, "disable-1", previous.UpdatedAtUtc.AddMinutes(1));
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "disable-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Disable,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            invalidResult,
            null,
            publication.Revision,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            invalidResult.UpdatedAtUtc.AddSeconds(1));

        var validation = GovernedLoopRevisionContractValidator.Validate(evidence);

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition);
    }

    [Fact]
    public void Stale_publication_conflict_needs_no_fabricated_validation_evidence()
    {
        var draft = GovernedLoopRevisionTestFixture.Revision(1);
        var previous = GovernedLoopRevisionTestFixture.DraftHead(draft);

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            "publish-stale",
            previous,
            previous,
            null,
            draft);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Null(evidence.PublicationValidationEvidenceHash);
    }

    [Theory]
    [InlineData(GovernedLoopRevisionOperationKind.Publish)]
    [InlineData(GovernedLoopRevisionOperationKind.Rollback)]
    public void Noncommitted_publication_operations_reject_fabricated_validation_evidence(GovernedLoopRevisionOperationKind kind)
    {
        var evidence = CreateNoncommittedEvidence(kind) with
        {
            PublicationValidationEvidenceHash = GovernedLoopRevisionTestFixture.ValidationHash,
        };

        var validation = GovernedLoopRevisionContractValidator.Validate(evidence);

        Assert.Contains(validation.Errors, error => error.Path == "$.publicationValidationEvidenceHash");
    }

    [Fact]
    public void Missing_rollback_source_receipt_retains_requested_pin_without_fabricated_validation_evidence()
    {
        var current = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(2, 'e'), "publish-current");
        var requestedSource = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-missing");
        var candidate = GovernedLoopRevisionTestFixture.Revision(3);
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, version: 4, operationId: "publish-current");

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.NotFound,
            GovernedLoopRevisionOperationFailureCode.PublicationNotFound,
            "rollback-missing",
            previous,
            previous,
            candidate,
            current.Revision,
            requestedSource);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
        Assert.Equal(requestedSource, evidence.RollbackSourcePublication);
        Assert.Null(evidence.PublicationValidationEvidenceHash);
    }

    [Fact]
    public void Rollback_may_select_current_publication_to_abandon_a_newer_draft()
    {
        var current = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-current");
        var currentDraft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var candidate = GovernedLoopRevisionTestFixture.Revision(3);
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, currentDraft, 3, "replace-1");
        var resultPin = GovernedLoopRevisionTestFixture.Pin(candidate, "rollback-1");
        var result = GovernedLoopRevisionTestFixture.PublishedHead(resultPin, version: 4, operationId: "rollback-1", updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));

        var evidence = GovernedLoopRevisionTestFixture.Evidence(
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            "rollback-1",
            previous,
            result,
            candidate,
            currentDraft,
            current,
            GovernedLoopRevisionTestFixture.ValidationHash);

        Assert.True(GovernedLoopRevisionContractValidator.Validate(evidence).IsValid);
    }

    [Fact]
    public void Rollback_candidate_must_be_distinct_from_both_current_heads_and_the_historical_source()
    {
        var currentRevision = GovernedLoopRevisionTestFixture.Revision(1);
        var current = GovernedLoopRevisionTestFixture.Pin(currentRevision, "publish-current");
        var currentDraft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(3), "publish-historical");
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, currentDraft, 3, "replace-1");
        var result = GovernedLoopRevisionTestFixture.PublishedHead(
            GovernedLoopRevisionTestFixture.Pin(currentRevision, "rollback-1"),
            version: 4,
            operationId: "rollback-1",
            updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "rollback-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            result,
            currentRevision,
            currentDraft,
            historical,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            GovernedLoopRevisionTestFixture.ValidationHash,
            result.UpdatedAtUtc.AddSeconds(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(evidence).Errors, error => error.Code is GovernedLoopRevisionValidationErrorCode.InvalidLineage or GovernedLoopRevisionValidationErrorCode.PublicationPinChanged);
    }

    [Fact]
    public void Rollback_evidence_source_must_use_a_prior_distinct_operation()
    {
        var source = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "rollback-1");
        var current = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(2, 'e'), "publish-current");
        var candidate = GovernedLoopRevisionTestFixture.Revision(3);
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(current, version: 3, operationId: "publish-current");
        var result = GovernedLoopRevisionTestFixture.PublishedHead(
            GovernedLoopRevisionTestFixture.Pin(candidate, "rollback-1"),
            version: 4,
            operationId: "rollback-1",
            updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            "rollback-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            result,
            candidate,
            current.Revision,
            source,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            GovernedLoopRevisionTestFixture.ValidationHash,
            result.UpdatedAtUtc.AddSeconds(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(evidence).Errors, error => error.Path.EndsWith("publicationOperationId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(GovernedLoopRevisionOperationKind.CreateDraft)]
    [InlineData(GovernedLoopRevisionOperationKind.ReplaceDraft)]
    [InlineData(GovernedLoopRevisionOperationKind.Publish)]
    [InlineData(GovernedLoopRevisionOperationKind.Disable)]
    [InlineData(GovernedLoopRevisionOperationKind.Archive)]
    [InlineData(GovernedLoopRevisionOperationKind.Rollback)]
    public void Noncommitted_operation_evidence_enforces_the_exact_kind_field_matrix(GovernedLoopRevisionOperationKind kind)
    {
        var canonical = CreateNoncommittedEvidence(kind);
        var malformed = kind switch
        {
            GovernedLoopRevisionOperationKind.CreateDraft => canonical with { TargetRevision = canonical.PreviousHead!.DraftRevision },
            GovernedLoopRevisionOperationKind.ReplaceDraft => canonical with { TargetRevision = null },
            GovernedLoopRevisionOperationKind.Publish => canonical with { CandidateRevision = GovernedLoopRevisionTestFixture.Revision(9, 'f') },
            GovernedLoopRevisionOperationKind.Disable => canonical with { CandidateRevision = GovernedLoopRevisionTestFixture.Revision(9, 'f') },
            GovernedLoopRevisionOperationKind.Archive => canonical with { CandidateRevision = GovernedLoopRevisionTestFixture.Revision(9, 'f') },
            GovernedLoopRevisionOperationKind.Rollback => canonical with { RollbackSourcePublication = null },
            _ => throw new InvalidOperationException()
        };

        Assert.True(GovernedLoopRevisionContractValidator.Validate(canonical).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.Validate(malformed).IsValid);
    }

    [Fact]
    public void Noncommitted_operation_cannot_reuse_the_operation_that_produced_its_previous_head()
    {
        var previous = GovernedLoopRevisionTestFixture.DraftHead();
        var evidence = new GovernedLoopRevisionOperationEvidence(
            1,
            previous.LastOperationId,
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            previous,
            previous,
            null,
            previous.DraftRevision,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            previous.UpdatedAtUtc.AddSeconds(1));

        Assert.Contains(GovernedLoopRevisionContractValidator.Validate(evidence).Errors, error => error.Path == "$.operationId");
    }

    [Theory]
    [InlineData(GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.RevisionNotFound)]
    [InlineData(GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.ArtifactLimitExceeded)]
    [InlineData(GovernedLoopRevisionOperationOutcome.LimitExceeded, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict)]
    [InlineData(GovernedLoopRevisionOperationOutcome.OutcomeUnknown, GovernedLoopRevisionOperationFailureCode.EvidenceLimitExceeded)]
    public void Durable_outcome_and_closed_failure_code_must_compose(
        GovernedLoopRevisionOperationOutcome outcome,
        GovernedLoopRevisionOperationFailureCode failureCode)
    {
        var candidate = GovernedLoopRevisionTestFixture.Revision(1);
        var result = GovernedLoopRevisionTestFixture.DraftHead(candidate);
        var malformed = new GovernedLoopRevisionOperationEvidence(
            1,
            "create-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            outcome,
            failureCode,
            null,
            outcome == GovernedLoopRevisionOperationOutcome.OutcomeUnknown ? null : null,
            candidate,
            null,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            result.UpdatedAtUtc);

        Assert.False(GovernedLoopRevisionContractValidator.Validate(malformed).IsValid);
    }

    [Fact]
    public void Lifecycle_transition_requires_exact_graph_contiguous_version_monotonic_time_and_distinct_operation()
    {
        var current = GovernedLoopRevisionTestFixture.DraftHead();
        var successorDraft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var valid = GovernedLoopRevisionTestFixture.DraftHead(successorDraft, 2, "replace-1", current.UpdatedAtUtc.AddMinutes(1));

        Assert.True(GovernedLoopRevisionContractValidator.ValidateTransition(current, valid).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.ValidateTransition(current, valid with { GraphId = "other-graph" }).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.ValidateTransition(current, valid with { LifecycleVersion = 3 }).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.ValidateTransition(current, valid with { UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(-1) }).IsValid);
        Assert.False(GovernedLoopRevisionContractValidator.ValidateTransition(current, valid with { LastOperationId = current.LastOperationId }).IsValid);
    }

    [Fact]
    public void Malformed_committed_evidence_rejects_inconsistent_terminal_claims()
    {
        var candidate = GovernedLoopRevisionTestFixture.Revision(1);
        var canonicalResult = GovernedLoopRevisionTestFixture.DraftHead(candidate);
        var mismatchedResult = canonicalResult with { LastOperationId = "other-operation" };
        var malformedCreate = new GovernedLoopRevisionOperationEvidence(
            1,
            "create-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            null,
            mismatchedResult,
            candidate,
            null,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            GovernedLoopRevisionTestFixture.ValidationHash,
            mismatchedResult.UpdatedAtUtc.AddSeconds(-1));
        var missingResult = malformedCreate with
        {
            FailureCode = GovernedLoopRevisionOperationFailureCode.None,
            ResultHead = null,
            PublicationValidationEvidenceHash = null,
            RecordedAtUtc = GovernedLoopRevisionTestFixture.CreatedAtUtc.AddSeconds(1),
        };

        var malformed = GovernedLoopRevisionContractValidator.Validate(malformedCreate);
        var absent = GovernedLoopRevisionContractValidator.Validate(missingResult);

        Assert.Contains(malformed.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidTimestamp);
        Assert.Contains(malformed.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition && error.Path == "$.failureCode");
        Assert.Contains(malformed.Errors, error => error.Path == "$.resultHead.lastOperationId");
        Assert.Contains(malformed.Errors, error => error.Path == "$.publicationValidationEvidenceHash");
        Assert.Contains(absent.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.ContractRequired && error.Path == "$.resultHead");
    }

    [Fact]
    public void Committed_publication_requires_validation_evidence_bound_to_its_exact_pin()
    {
        var draft = GovernedLoopRevisionTestFixture.Revision(1);
        var previous = GovernedLoopRevisionTestFixture.DraftHead(draft);
        var pin = GovernedLoopRevisionTestFixture.Pin(draft);
        var result = GovernedLoopRevisionTestFixture.PublishedHead(pin, version: 2, updatedAtUtc: previous.UpdatedAtUtc.AddMinutes(1));
        var missingProof = new GovernedLoopRevisionOperationEvidence(
            1,
            "publish-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            result,
            null,
            draft,
            null,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            result.UpdatedAtUtc.AddSeconds(1));

        var validation = GovernedLoopRevisionContractValidator.Validate(missingProof);

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.ContractRequired && error.Path == "$.publicationValidationEvidenceHash");
        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition && error.Path == "$.resultHead");
    }

    [Fact]
    public void Lifecycle_transitions_reject_cross_graph_and_illegal_posture_changes()
    {
        var currentDraft = GovernedLoopRevisionTestFixture.DraftHead();
        var otherGraphDraft = GovernedLoopRevisionTestFixture.DraftHead(
            GovernedLoopRevisionTestFixture.Revision(2, 'e', "other-graph"),
            2,
            "replace-other",
            currentDraft.UpdatedAtUtc.AddMinutes(1));
        var publication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1));
        var published = GovernedLoopRevisionTestFixture.PublishedHead(publication);
        var returnedToDraft = GovernedLoopRevisionTestFixture.DraftHead(
            GovernedLoopRevisionTestFixture.Revision(2, 'e'),
            published.LifecycleVersion + 1,
            "return-to-draft",
            published.UpdatedAtUtc.AddMinutes(1));
        var replacementPublication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(3, 'f'), "publish-other");
        var invalidDisable = GovernedLoopRevisionTestFixture.DisabledHead(
            replacementPublication,
            version: published.LifecycleVersion + 1,
            operationId: "disable-other",
            updatedAtUtc: published.UpdatedAtUtc.AddMinutes(1));
        var invalidArchive = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            "graph",
            published.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Archived,
            null,
            replacementPublication,
            "archive-other",
            published.UpdatedAtUtc.AddMinutes(1));
        var disabled = GovernedLoopRevisionTestFixture.DisabledHead(publication);
        var disabledToDraft = returnedToDraft with
        {
            LifecycleVersion = disabled.LifecycleVersion + 1,
            LastOperationId = "disabled-to-draft",
            UpdatedAtUtc = disabled.UpdatedAtUtc.AddMinutes(1),
        };
        var unchanged = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            LastOperationId = "no-state-change",
            UpdatedAtUtc = published.UpdatedAtUtc.AddMinutes(1),
        };

        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(currentDraft, otherGraphDraft).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.GraphMismatch && error.Path == "$.next.graphId");
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(published, returnedToDraft).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.IllegalTransition && error.Path == "$.next.status");
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(published, invalidDisable).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.PublicationPinChanged);
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(published, invalidArchive).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.PublicationPinChanged);
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(disabled, disabledToDraft).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopRevisionContractValidator.ValidateTransition(published, unchanged).Errors, error => error.Code == GovernedLoopRevisionValidationErrorCode.IllegalTransition && error.Path == "$");
    }

    private static GovernedLoopRevisionOperationEvidence CreateNoncommittedEvidence(GovernedLoopRevisionOperationKind kind)
    {
        var publication = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(1), "publish-current");
        var draft = GovernedLoopRevisionTestFixture.Revision(2, 'e');
        var candidate = GovernedLoopRevisionTestFixture.Revision(3, 'f');
        var historical = GovernedLoopRevisionTestFixture.Pin(GovernedLoopRevisionTestFixture.Revision(4, 'f'), "publish-historical");
        var previous = GovernedLoopRevisionTestFixture.PublishedHead(publication, draft, 3, "replace-current");
        var operationId = $"{kind.ToString().ToLowerInvariant()}-conflict";
        var (candidateRevision, targetRevision, rollbackSource) = kind switch
        {
            GovernedLoopRevisionOperationKind.CreateDraft => (candidate, null, null),
            GovernedLoopRevisionOperationKind.ReplaceDraft => (candidate, draft, null),
            GovernedLoopRevisionOperationKind.Publish => (null, draft, null),
            GovernedLoopRevisionOperationKind.Disable => (null, publication.Revision, null),
            GovernedLoopRevisionOperationKind.Archive => (null, publication.Revision, null),
            GovernedLoopRevisionOperationKind.Rollback => (candidate, draft, historical),
            _ => throw new InvalidOperationException()
        };
        return new GovernedLoopRevisionOperationEvidence(
            1,
            operationId,
            "actor-1",
            GovernedLoopRevisionTestFixture.RequestHash,
            kind,
            GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict,
            previous,
            previous,
            candidateRevision,
            targetRevision,
            rollbackSource,
            GovernedLoopRevisionTestFixture.AuthorityHash,
            null,
            previous.UpdatedAtUtc.AddSeconds(1));
    }
}
