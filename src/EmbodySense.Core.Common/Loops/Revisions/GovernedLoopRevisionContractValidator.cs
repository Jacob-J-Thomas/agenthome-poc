using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Validates bounded schema-1 revision artifacts, pins, heads, operation evidence, and head transitions.</summary>
public static class GovernedLoopRevisionContractValidator
{
    /// <summary>Validates one immutable revision artifact.</summary>
    /// <param name="artifact">The candidate artifact.</param>
    /// <returns>A bounded deterministic validation result.</returns>
    public static GovernedLoopRevisionValidationResult Validate(GovernedLoopRevisionArtifact? artifact)
    {
        var errors = new List<GovernedLoopRevisionValidationError>();
        if (artifact is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$", "A revision artifact is required.");
            return Result(errors);
        }

        ValidateSchema(artifact.SchemaVersion, errors);
        ValidateReference(artifact.Revision, "$.revision", errors);
        ValidateOptionalReference(artifact.PredecessorRevision, "$.predecessorRevision", errors);
        ValidateOptionalPin(artifact.RollbackSourcePublication, "$.rollbackSourcePublication", errors);
        ValidateIdentifier(artifact.CreationOperationId, "$.creationOperationId", errors);
        ValidateActorId(artifact.CreatedByActorId, "$.createdByActorId", errors);
        ValidateTimestamp(artifact.CreatedAtUtc, "$.createdAtUtc", errors);

        if (artifact.PredecessorRevision is not null)
        {
            ValidateSameGraph(artifact.Revision, artifact.PredecessorRevision, "$.predecessorRevision", errors);
            if (GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(artifact.Revision, artifact.PredecessorRevision))
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.predecessorRevision", "A successor must use a revision identifier distinct from its predecessor.");
            }
        }

        if (artifact.RollbackSourcePublication is not null)
        {
            var rollbackSourceRevision = artifact.RollbackSourcePublication.Revision;
            if (artifact.PredecessorRevision is null)
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication", "A rollback successor requires an exact predecessor revision.");
            }

            ValidateSameGraph(artifact.Revision, rollbackSourceRevision, "$.rollbackSourcePublication.revision", errors);
            ValidateConsistentRevisionContent(artifact.PredecessorRevision, rollbackSourceRevision, "$.rollbackSourcePublication.revision", errors);
            if (GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(artifact.Revision, rollbackSourceRevision))
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication.revision", "A rollback successor must have a distinct immutable revision identity.");
            }

            if (artifact.Revision is not null
                && rollbackSourceRevision is not null
                && !string.Equals(artifact.Revision.ExecutableHash, rollbackSourceRevision.ExecutableHash, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication.revision.executableHash", "A rollback successor must retain the exact selected historical executable content.");
            }

            if (string.Equals(artifact.CreationOperationId, artifact.RollbackSourcePublication.PublicationOperationId, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication.publicationOperationId", "A rollback source publication must predate and use an operation distinct from its successor creation.");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one exact publication pin.</summary>
    /// <param name="pin">The candidate publication pin.</param>
    /// <returns>A bounded deterministic validation result.</returns>
    public static GovernedLoopRevisionValidationResult Validate(GovernedLoopRevisionPublicationPin? pin)
    {
        var errors = new List<GovernedLoopRevisionValidationError>();
        if (pin is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$", "A publication pin is required.");
            return Result(errors);
        }

        ValidateSchema(pin.SchemaVersion, errors);
        ValidateReference(pin.Revision, "$.revision", errors);
        ValidateIdentifier(pin.PublicationOperationId, "$.publicationOperationId", errors);
        ValidateHash(pin.ValidationEvidenceHash, "$.validationEvidenceHash", errors);
        return Result(errors);
    }

    /// <summary>Validates one optimistic lifecycle-head projection.</summary>
    /// <param name="head">The candidate lifecycle head.</param>
    /// <returns>A bounded deterministic validation result.</returns>
    public static GovernedLoopRevisionValidationResult Validate(GovernedLoopRevisionLifecycleHead? head)
    {
        var errors = new List<GovernedLoopRevisionValidationError>();
        if (head is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$", "A lifecycle head is required.");
            return Result(errors);
        }

        ValidateSchema(head.SchemaVersion, errors);
        ValidateIdentifier(head.GraphId, "$.graphId", errors);
        if (!GovernedLoopRevisionContractGuard.IsVersion(head.LifecycleVersion))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLifecycleVersion, "$.lifecycleVersion", "Lifecycle version must be positive and within the finite schema bound.");
        }

        if (!Enum.IsDefined(head.Status) || head.Status == GovernedLoopRevisionLifecycleStatus.Unknown)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidEnumeration, "$.status", "A supported lifecycle status is required.");
        }

        ValidateOptionalReference(head.DraftRevision, "$.draftRevision", errors);
        ValidateOptionalPin(head.PublishedRevision, "$.publishedRevision", errors);
        ValidateIdentifier(head.LastOperationId, "$.lastOperationId", errors);
        ValidateTimestamp(head.UpdatedAtUtc, "$.updatedAtUtc", errors);

        if (head.DraftRevision is not null)
        {
            ValidateReferenceGraph(head.DraftRevision, head.GraphId, "$.draftRevision", errors);
        }

        if (head.PublishedRevision is not null)
        {
            ValidateReferenceGraph(head.PublishedRevision.Revision, head.GraphId, "$.publishedRevision.revision", errors);
        }

        if (head.DraftRevision is not null
            && head.PublishedRevision is not null
            && GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(head.DraftRevision, head.PublishedRevision.Revision))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.draftRevision", "Draft and published heads must identify distinct immutable revisions.");
        }

        ValidateHeadComposition(head, errors);
        return Result(errors);
    }

    /// <summary>Validates one durable lifecycle operation-evidence contract.</summary>
    /// <param name="evidence">The candidate operation evidence.</param>
    /// <returns>A bounded deterministic validation result.</returns>
    public static GovernedLoopRevisionValidationResult Validate(GovernedLoopRevisionOperationEvidence? evidence)
    {
        var errors = new List<GovernedLoopRevisionValidationError>();
        if (evidence is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$", "Revision operation evidence is required.");
            return Result(errors);
        }

        ValidateSchema(evidence.SchemaVersion, errors);
        ValidateIdentifier(evidence.OperationId, "$.operationId", errors);
        ValidateActorId(evidence.ActorId, "$.actorId", errors);
        ValidateHash(evidence.RequestHash, "$.requestHash", errors);
        ValidateEnumeration(evidence.Kind, "$.kind", errors);
        ValidateEnumeration(evidence.Outcome, "$.outcome", errors);
        ValidateEnumeration(evidence.FailureCode, "$.failureCode", errors, allowNone: true);
        ValidateOptionalHead(evidence.PreviousHead, "$.previousHead", errors);
        ValidateOptionalHead(evidence.ResultHead, "$.resultHead", errors);
        ValidateOptionalReference(evidence.CandidateRevision, "$.candidateRevision", errors);
        ValidateOptionalReference(evidence.TargetRevision, "$.targetRevision", errors);
        ValidateOptionalPin(evidence.RollbackSourcePublication, "$.rollbackSourcePublication", errors);
        ValidateHash(evidence.AuthorityEvidenceHash, "$.authorityEvidenceHash", errors);
        if (evidence.PublicationValidationEvidenceHash is not null)
        {
            ValidateHash(evidence.PublicationValidationEvidenceHash, "$.publicationValidationEvidenceHash", errors);
        }

        ValidateTimestamp(evidence.RecordedAtUtc, "$.recordedAtUtc", errors);
        ValidateOperationEvidenceComposition(evidence, errors);
        return Result(errors);
    }

    /// <summary>Validates that a proposed lifecycle head is a legal contiguous successor.</summary>
    /// <param name="current">The exact current lifecycle head.</param>
    /// <param name="next">The proposed successor lifecycle head.</param>
    /// <returns>A bounded deterministic validation result.</returns>
    public static GovernedLoopRevisionValidationResult ValidateTransition(GovernedLoopRevisionLifecycleHead? current, GovernedLoopRevisionLifecycleHead? next)
    {
        var errors = new List<GovernedLoopRevisionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$", "Current and successor lifecycle heads are required.");
            return Result(errors);
        }

        AddNested(errors, Validate(current), "$.current");
        AddNested(errors, Validate(next), "$.next");
        if (errors.Count > 0)
        {
            return Result(errors);
        }

        ValidateTransitionRevisionContent(current, next, errors);

        if (!string.Equals(current.GraphId, next.GraphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.GraphMismatch, "$.next.graphId", "Lifecycle successors must retain the exact graph identity.");
        }

        if (current.LifecycleVersion >= GovernedLoopRevisionContractLimits.MaxLifecycleVersion
            || next.LifecycleVersion != current.LifecycleVersion + 1)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidSuccessorVersion, "$.next.lifecycleVersion", "A lifecycle successor version must be exactly one greater than its predecessor.");
        }

        if (next.UpdatedAtUtc < current.UpdatedAtUtc)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$.next.updatedAtUtc", "A lifecycle successor timestamp cannot precede its predecessor.");
        }

        if (string.Equals(current.LastOperationId, next.LastOperationId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$.next.lastOperationId", "A committed successor must name a distinct operation.");
        }

        if (current.Status == GovernedLoopRevisionLifecycleStatus.Archived)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$.current.status", "An archived lifecycle is terminal.");
            return Result(errors);
        }

        ValidateStatusTransition(current, next, errors);
        return Result(errors);
    }

    private static void ValidateOperationEvidenceComposition(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        ValidateEvidenceRevisionContent(evidence, errors);
        var graphId = FirstGraphId(evidence);
        if (graphId is not null)
        {
            ValidateEvidenceGraph(evidence, graphId, errors);
        }

        if (evidence.ResultHead is not null && evidence.ResultHead.UpdatedAtUtc > evidence.RecordedAtUtc)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidTimestamp, "$.recordedAtUtc", "Operation evidence cannot precede its exact resulting head.");
        }

        if (evidence.Outcome == GovernedLoopRevisionOperationOutcome.Committed)
        {
            ValidateCommittedEvidence(evidence, errors);
        }
        else
        {
            ValidateNonCommittedEvidence(evidence, errors);
        }

        ValidateOperationShape(evidence, errors);
    }

    private static void ValidateCommittedEvidence(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.FailureCode != GovernedLoopRevisionOperationFailureCode.None)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.failureCode", "Committed operation evidence cannot retain a failure code.");
        }

        if (evidence.ResultHead is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$.resultHead", "Committed operation evidence requires the exact resulting head.");
            return;
        }

        if (!string.Equals(evidence.ResultHead.LastOperationId, evidence.OperationId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead.lastOperationId", "The committed resulting head must name the evidence operation.");
        }

        if (evidence.PreviousHead is null)
        {
            if (evidence.Kind != GovernedLoopRevisionOperationKind.CreateDraft || evidence.ResultHead.LifecycleVersion != 1)
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidSuccessorVersion, "$.resultHead.lifecycleVersion", "Only first-draft creation may commit without a previous head, and it must produce lifecycle version 1.");
            }
        }
        else
        {
            AddNested(errors, ValidateTransition(evidence.PreviousHead, evidence.ResultHead), "$.headTransition");
        }

        ValidateCommittedOperationKind(evidence, errors);
    }

    private static void ValidateCommittedOperationKind(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        var previous = evidence.PreviousHead;
        var result = evidence.ResultHead;
        if (result is null)
        {
            return;
        }

        switch (evidence.Kind)
        {
            case GovernedLoopRevisionOperationKind.CreateDraft:
                if (previous is not null
                    || evidence.CandidateRevision is null
                    || evidence.TargetRevision is not null
                    || result.Status != GovernedLoopRevisionLifecycleStatus.Draft
                    || !GovernedLoopRevisionContractGuard.IsSameReference(result.DraftRevision, evidence.CandidateRevision)
                    || result.PublishedRevision is not null)
                {
                    Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed first-draft creation must publish only its exact candidate as draft lifecycle version 1.");
                }

                break;
            case GovernedLoopRevisionOperationKind.ReplaceDraft:
                ValidateCommittedDraftReplacement(evidence, previous, result, errors);
                break;
            case GovernedLoopRevisionOperationKind.Publish:
                ValidateCommittedPublication(evidence, previous, result, errors);
                break;
            case GovernedLoopRevisionOperationKind.Disable:
                ValidateCommittedDisablement(evidence, previous, result, errors);
                break;
            case GovernedLoopRevisionOperationKind.Archive:
                ValidateCommittedArchival(evidence, previous, result, errors);
                break;
            case GovernedLoopRevisionOperationKind.Rollback:
                ValidateCommittedRollback(evidence, previous, result, errors);
                break;
        }
    }

    private static void ValidateCommittedDraftReplacement(
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead result,
        List<GovernedLoopRevisionValidationError> errors)
    {
        var expectedTarget = previous?.DraftRevision ?? previous?.PublishedRevision?.Revision;
        if (previous is null
            || evidence.CandidateRevision is null
            || evidence.TargetRevision is null
            || expectedTarget is null
            || !GovernedLoopRevisionContractGuard.IsSameReference(evidence.TargetRevision, expectedTarget)
            || CandidateReusesCurrentRevisionIdentity(evidence)
            || !GovernedLoopRevisionContractGuard.IsSameReference(result.DraftRevision, evidence.CandidateRevision)
            || result.Status != previous.Status
            || !Equals(result.PublishedRevision, previous.PublishedRevision))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed draft replacement must target the exact current draft or publication, set a distinct candidate draft, and preserve publication posture.");
        }
    }

    private static void ValidateCommittedPublication(
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead result,
        List<GovernedLoopRevisionValidationError> errors)
    {
        if (previous?.DraftRevision is null
            || evidence.CandidateRevision is not null
            || evidence.TargetRevision is null
            || !GovernedLoopRevisionContractGuard.IsSameReference(evidence.TargetRevision, previous.DraftRevision)
            || result.Status != GovernedLoopRevisionLifecycleStatus.Published
            || result.DraftRevision is not null
            || !PublicationMatchesOperation(result.PublishedRevision, evidence.TargetRevision, evidence.OperationId, evidence.PublicationValidationEvidenceHash))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed publication must pin the exact current draft with its operation and validation evidence, then clear the draft head.");
        }
    }

    private static void ValidateCommittedDisablement(
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead result,
        List<GovernedLoopRevisionValidationError> errors)
    {
        if (previous?.Status != GovernedLoopRevisionLifecycleStatus.Published
            || previous.PublishedRevision is null
            || evidence.CandidateRevision is not null
            || evidence.TargetRevision is null
            || !GovernedLoopRevisionContractGuard.IsSameReference(evidence.TargetRevision, previous.PublishedRevision.Revision)
            || result.Status != GovernedLoopRevisionLifecycleStatus.Disabled
            || !Equals(result.DraftRevision, previous.DraftRevision)
            || !Equals(result.PublishedRevision, previous.PublishedRevision))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed disablement may only change an exact active publication's lifecycle posture.");
        }
    }

    private static void ValidateCommittedArchival(
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead result,
        List<GovernedLoopRevisionValidationError> errors)
    {
        if (previous?.PublishedRevision is null
            || previous.Status is not GovernedLoopRevisionLifecycleStatus.Published and not GovernedLoopRevisionLifecycleStatus.Disabled
            || evidence.CandidateRevision is not null
            || evidence.TargetRevision is null
            || !GovernedLoopRevisionContractGuard.IsSameReference(evidence.TargetRevision, previous.PublishedRevision.Revision)
            || result.Status != GovernedLoopRevisionLifecycleStatus.Archived
            || result.DraftRevision is not null
            || !Equals(result.PublishedRevision, previous.PublishedRevision))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed archival must retain the exact current publication, clear any draft head, and enter terminal posture.");
        }
    }

    private static void ValidateCommittedRollback(
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead result,
        List<GovernedLoopRevisionValidationError> errors)
    {
        var rollbackSourceRevision = evidence.RollbackSourcePublication?.Revision;
        if (previous?.PublishedRevision is null
            || previous.Status is not GovernedLoopRevisionLifecycleStatus.Published and not GovernedLoopRevisionLifecycleStatus.Disabled
            || evidence.CandidateRevision is null
            || evidence.TargetRevision is null
            || rollbackSourceRevision is null
            || !GovernedLoopRevisionContractGuard.IsSameReference(evidence.TargetRevision, previous.DraftRevision ?? previous.PublishedRevision.Revision)
            || CandidateReusesCurrentRevisionIdentity(evidence)
            || GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(evidence.CandidateRevision, rollbackSourceRevision)
            || !string.Equals(evidence.CandidateRevision.ExecutableHash, rollbackSourceRevision.ExecutableHash, StringComparison.Ordinal)
            || result.Status != GovernedLoopRevisionLifecycleStatus.Published
            || result.DraftRevision is not null
            || !PublicationMatchesOperation(result.PublishedRevision, evidence.CandidateRevision, evidence.OperationId, evidence.PublicationValidationEvidenceHash))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "Committed rollback must replace the current publication with a distinct immutable successor and cite an exact historical publication.");
        }
    }

    private static bool PublicationMatchesOperation(
        GovernedLoopRevisionPublicationPin? pin,
        GovernedLoopRevisionReference revision,
        string operationId,
        string? validationEvidenceHash)
    {
        return pin is not null
            && GovernedLoopRevisionContractGuard.IsSameReference(pin.Revision, revision)
            && string.Equals(pin.PublicationOperationId, operationId, StringComparison.Ordinal)
            && string.Equals(pin.ValidationEvidenceHash, validationEvidenceHash, StringComparison.Ordinal);
    }

    private static void ValidateNonCommittedEvidence(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        var supportedFailure = evidence.Outcome switch
        {
            GovernedLoopRevisionOperationOutcome.Conflict => evidence.FailureCode is GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict or GovernedLoopRevisionOperationFailureCode.OperationIntentConflict or GovernedLoopRevisionOperationFailureCode.LifecycleArchived,
            GovernedLoopRevisionOperationOutcome.NotFound => evidence.FailureCode is GovernedLoopRevisionOperationFailureCode.LifecycleNotFound or GovernedLoopRevisionOperationFailureCode.RevisionNotFound or GovernedLoopRevisionOperationFailureCode.PublicationNotFound,
            GovernedLoopRevisionOperationOutcome.LimitExceeded => evidence.FailureCode is GovernedLoopRevisionOperationFailureCode.ArtifactLimitExceeded or GovernedLoopRevisionOperationFailureCode.EvidenceLimitExceeded or GovernedLoopRevisionOperationFailureCode.LifecycleVersionLimitExceeded,
            GovernedLoopRevisionOperationOutcome.OutcomeUnknown => evidence.FailureCode == GovernedLoopRevisionOperationFailureCode.OutcomeUnresolved,
            _ => false
        };
        if (!supportedFailure)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.failureCode", "Operation outcome and closed failure code do not compose.");
        }

        if (evidence.Outcome != GovernedLoopRevisionOperationOutcome.OutcomeUnknown
            && !Equals(evidence.PreviousHead, evidence.ResultHead))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.resultHead", "A conclusive non-commit must retain the exact observed previous head.");
        }

        if (evidence.PreviousHead is not null
            && string.Equals(evidence.OperationId, evidence.PreviousHead.LastOperationId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.operationId", "A new noncommitted operation cannot reuse the operation that produced its observed previous head.");
        }
    }

    private static void ValidateOperationShape(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        var isPublicationOperation = evidence.Kind is GovernedLoopRevisionOperationKind.Publish or GovernedLoopRevisionOperationKind.Rollback;
        if (evidence.PublicationValidationEvidenceHash is not null
            && (!isPublicationOperation || evidence.Outcome != GovernedLoopRevisionOperationOutcome.Committed))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.publicationValidationEvidenceHash", "Only committed publication and rollback may retain exact validation evidence.");
        }

        if (isPublicationOperation
            && evidence.Outcome == GovernedLoopRevisionOperationOutcome.Committed
            && evidence.PublicationValidationEvidenceHash is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$.publicationValidationEvidenceHash", "Committed publication and rollback require exact validation evidence.");
        }

        if (evidence.Kind == GovernedLoopRevisionOperationKind.Rollback)
        {
            if (evidence.CandidateRevision is null || evidence.TargetRevision is null || evidence.RollbackSourcePublication is null)
            {
                Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$.rollbackSourcePublication", "Rollback requires an exact successor candidate, current target, and requested historical publication source.");
            }
        }
        else if (evidence.RollbackSourcePublication is not null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication", "Only rollback may cite a historical publication source.");
        }

        switch (evidence.Kind)
        {
            case GovernedLoopRevisionOperationKind.CreateDraft:
                RequireCandidate(evidence, errors);
                RejectTarget(evidence, errors);
                break;
            case GovernedLoopRevisionOperationKind.ReplaceDraft:
                RequireCandidate(evidence, errors);
                RequireTarget(evidence, errors);
                ValidateCandidateDoesNotReuseCurrentRevisionIdentity(evidence, errors);
                break;
            case GovernedLoopRevisionOperationKind.Publish:
                RejectCandidate(evidence, errors);
                RequireTarget(evidence, errors);
                break;
            case GovernedLoopRevisionOperationKind.Disable:
            case GovernedLoopRevisionOperationKind.Archive:
                RejectCandidate(evidence, errors);
                RequireTarget(evidence, errors);
                break;
            case GovernedLoopRevisionOperationKind.Rollback:
                RequireCandidate(evidence, errors);
                RequireTarget(evidence, errors);
                ValidateCandidateDoesNotReuseCurrentRevisionIdentity(evidence, errors);
                if (evidence.RollbackSourcePublication is not null
                    && string.Equals(evidence.OperationId, evidence.RollbackSourcePublication.PublicationOperationId, StringComparison.Ordinal))
                {
                    Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.rollbackSourcePublication.publicationOperationId", "A rollback source publication must predate and use an operation distinct from the rollback successor.");
                }

                break;
        }
    }

    private static void RejectCandidate(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.CandidateRevision is not null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.candidateRevision", "The lifecycle operation cannot retain a candidate revision.");
        }
    }

    private static void RejectTarget(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.TargetRevision is not null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$.targetRevision", "The lifecycle operation cannot retain a target revision.");
        }
    }

    private static void ValidateCandidateDoesNotReuseCurrentRevisionIdentity(
        GovernedLoopRevisionOperationEvidence evidence,
        List<GovernedLoopRevisionValidationError> errors)
    {
        if (CandidateReusesCurrentRevisionIdentity(evidence))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, "$.candidateRevision", "A successor candidate must use a revision identifier distinct from every current or selected source revision.");
        }
    }

    private static bool CandidateReusesCurrentRevisionIdentity(GovernedLoopRevisionOperationEvidence evidence)
    {
        var candidate = evidence.CandidateRevision;
        return candidate is not null
            && (GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(candidate, evidence.TargetRevision)
                || GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(candidate, evidence.PreviousHead?.DraftRevision)
                || GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(candidate, evidence.PreviousHead?.PublishedRevision?.Revision)
                || GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(candidate, evidence.RollbackSourcePublication?.Revision));
    }

    private static void ValidateEvidenceRevisionContent(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        var revisions = new (GovernedLoopRevisionReference? Revision, string Path)[]
        {
            (evidence.PreviousHead?.DraftRevision, "$.previousHead.draftRevision"),
            (evidence.PreviousHead?.PublishedRevision?.Revision, "$.previousHead.publishedRevision.revision"),
            (evidence.ResultHead?.DraftRevision, "$.resultHead.draftRevision"),
            (evidence.ResultHead?.PublishedRevision?.Revision, "$.resultHead.publishedRevision.revision"),
            (evidence.CandidateRevision, "$.candidateRevision"),
            (evidence.TargetRevision, "$.targetRevision"),
            (evidence.RollbackSourcePublication?.Revision, "$.rollbackSourcePublication.revision")
        };
        for (var leftIndex = 0; leftIndex < revisions.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < revisions.Length; rightIndex++)
            {
                ValidateConsistentRevisionContent(
                    revisions[leftIndex].Revision,
                    revisions[rightIndex].Revision,
                    revisions[rightIndex].Path,
                    errors);
            }
        }
    }

    private static void RequireCandidate(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.CandidateRevision is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$.candidateRevision", "The lifecycle operation requires an exact immutable candidate revision.");
        }
    }

    private static void RequireTarget(GovernedLoopRevisionOperationEvidence evidence, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.TargetRevision is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, "$.targetRevision", "The lifecycle operation requires an exact existing target revision.");
        }
    }

    private static string? FirstGraphId(GovernedLoopRevisionOperationEvidence evidence)
        => evidence.PreviousHead?.GraphId
            ?? evidence.ResultHead?.GraphId
            ?? evidence.CandidateRevision?.GraphId
            ?? evidence.TargetRevision?.GraphId
            ?? evidence.RollbackSourcePublication?.Revision?.GraphId;

    private static void ValidateEvidenceGraph(GovernedLoopRevisionOperationEvidence evidence, string graphId, List<GovernedLoopRevisionValidationError> errors)
    {
        if (evidence.PreviousHead is not null && !string.Equals(evidence.PreviousHead.GraphId, graphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.GraphMismatch, "$.previousHead.graphId", "Operation evidence must identify one exact graph.");
        }

        if (evidence.ResultHead is not null && !string.Equals(evidence.ResultHead.GraphId, graphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.GraphMismatch, "$.resultHead.graphId", "Operation evidence must identify one exact graph.");
        }

        ValidateReferenceGraph(evidence.CandidateRevision, graphId, "$.candidateRevision", errors);
        ValidateReferenceGraph(evidence.TargetRevision, graphId, "$.targetRevision", errors);
        ValidateReferenceGraph(evidence.RollbackSourcePublication?.Revision, graphId, "$.rollbackSourcePublication.revision", errors);
    }

    private static void ValidateHeadComposition(GovernedLoopRevisionLifecycleHead head, List<GovernedLoopRevisionValidationError> errors)
    {
        var valid = head.Status switch
        {
            GovernedLoopRevisionLifecycleStatus.Draft => head.DraftRevision is not null && head.PublishedRevision is null,
            GovernedLoopRevisionLifecycleStatus.Published => head.PublishedRevision is not null,
            GovernedLoopRevisionLifecycleStatus.Disabled => head.PublishedRevision is not null,
            GovernedLoopRevisionLifecycleStatus.Archived => head.PublishedRevision is not null && head.DraftRevision is null,
            _ => false
        };
        if (!valid)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHeadComposition, "$", "Lifecycle status does not compose with exact draft and publication heads.");
        }
    }

    private static void ValidateTransitionRevisionContent(
        GovernedLoopRevisionLifecycleHead current,
        GovernedLoopRevisionLifecycleHead next,
        List<GovernedLoopRevisionValidationError> errors)
    {
        var revisions = new (GovernedLoopRevisionReference? Revision, string Path)[]
        {
            (current.DraftRevision, "$.current.draftRevision"),
            (current.PublishedRevision?.Revision, "$.current.publishedRevision.revision"),
            (next.DraftRevision, "$.next.draftRevision"),
            (next.PublishedRevision?.Revision, "$.next.publishedRevision.revision")
        };
        for (var leftIndex = 0; leftIndex < revisions.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < revisions.Length; rightIndex++)
            {
                ValidateConsistentRevisionContent(
                    revisions[leftIndex].Revision,
                    revisions[rightIndex].Revision,
                    revisions[rightIndex].Path,
                    errors);
            }
        }

        if (current.PublishedRevision is not null
            && next.PublishedRevision is not null
            && GovernedLoopRevisionContractGuard.IsSameRevisionIdentity(current.PublishedRevision.Revision, next.PublishedRevision.Revision)
            && !Equals(current.PublishedRevision, next.PublishedRevision))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.PublicationPinChanged, "$.next.publishedRevision", "One immutable published revision identity cannot be rebound to different publication evidence.");
        }
    }

    private static void ValidateStatusTransition(GovernedLoopRevisionLifecycleHead current, GovernedLoopRevisionLifecycleHead next, List<GovernedLoopRevisionValidationError> errors)
    {
        if (next.Status == GovernedLoopRevisionLifecycleStatus.Draft && current.Status != GovernedLoopRevisionLifecycleStatus.Draft)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$.next.status", "A lifecycle with publication history cannot return to draft-only posture.");
        }

        if (next.Status == GovernedLoopRevisionLifecycleStatus.Disabled
            && (current.Status != GovernedLoopRevisionLifecycleStatus.Published || !Equals(current.PublishedRevision, next.PublishedRevision)))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.PublicationPinChanged, "$.next.publishedRevision", "Disablement must retain the exact current publication pin.");
        }

        if (next.Status == GovernedLoopRevisionLifecycleStatus.Archived
            && (current.PublishedRevision is null || !Equals(current.PublishedRevision, next.PublishedRevision)))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.PublicationPinChanged, "$.next.publishedRevision", "Archival must retain the exact current publication pin.");
        }

        if (current.Status == GovernedLoopRevisionLifecycleStatus.Disabled
            && next.Status is not GovernedLoopRevisionLifecycleStatus.Disabled and not GovernedLoopRevisionLifecycleStatus.Published and not GovernedLoopRevisionLifecycleStatus.Archived)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$.next.status", "A disabled lifecycle may only retain its posture, publish a successor, or archive.");
        }

        if (Equals(current.DraftRevision, next.DraftRevision)
            && Equals(current.PublishedRevision, next.PublishedRevision)
            && current.Status == next.Status)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.IllegalTransition, "$", "A committed lifecycle successor must change an exact head or lifecycle posture.");
        }
    }

    private static void ValidateSchema(int schemaVersion, List<GovernedLoopRevisionValidationError> errors)
    {
        if (schemaVersion != GovernedLoopRevisionContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.UnsupportedSchemaVersion, "$.schemaVersion", "Schema version must be 1.");
        }
    }

    private static void ValidateReference(GovernedLoopRevisionReference? revision, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (revision is null)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.ContractRequired, path, "An exact revision reference is required.");
            return;
        }

        if (revision.SchemaVersion != GovernedLoopRevisionContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.UnsupportedSchemaVersion, $"{path}.schemaVersion", "Revision-reference schema version must be 1.");
        }

        ValidateIdentifier(revision.GraphId, $"{path}.graphId", errors);
        ValidateIdentifier(revision.RevisionId, $"{path}.revisionId", errors);
        ValidateHash(revision.ExecutableHash, $"{path}.executableHash", errors);
    }

    private static void ValidateOptionalReference(GovernedLoopRevisionReference? revision, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (revision is not null)
        {
            ValidateReference(revision, path, errors);
        }
    }

    private static void ValidateOptionalPin(GovernedLoopRevisionPublicationPin? pin, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (pin is not null)
        {
            AddNested(errors, Validate(pin), path);
        }
    }

    private static void ValidateOptionalHead(GovernedLoopRevisionLifecycleHead? head, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (head is not null)
        {
            AddNested(errors, Validate(head), path);
        }
    }

    private static void ValidateIdentifier(string? value, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (!GovernedLoopRevisionContractGuard.IsIdentifier(value))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidIdentifier, path, "Identifier must be a bounded canonical lowercase ASCII token.");
        }
    }

    private static void ValidateActorId(string? value, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (!AuthorityActorId.TryParse(value, out _, out _))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidIdentifier, path, "Actor identifier must satisfy the bounded canonical authority actor contract.");
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (!GovernedLoopRevisionContractGuard.IsHash(value))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidHash, path, "Hash must be canonical lowercase SHA-256 hexadecimal.");
        }
    }

    private static void ValidateTimestamp(DateTimeOffset value, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (!GovernedLoopRevisionContractGuard.IsUtc(value))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidTimestamp, path, "Timestamp must be a non-default UTC value with zero offset.");
        }
    }

    private static void ValidateEnumeration<TEnum>(TEnum value, string path, List<GovernedLoopRevisionValidationError> errors, bool allowNone = false)
        where TEnum : struct, Enum
    {
        var number = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(value) || number == 0 || !allowNone && string.Equals(value.ToString(), nameof(GovernedLoopRevisionOperationFailureCode.None), StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidEnumeration, path, "A supported closed enumeration value is required.");
        }
    }

    private static void ValidateSameGraph(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (left is not null && right is not null && !string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.GraphMismatch, path, "Related revision references must identify one exact graph.");
        }
    }

    private static void ValidateConsistentRevisionContent(
        GovernedLoopRevisionReference? left,
        GovernedLoopRevisionReference? right,
        string path,
        List<GovernedLoopRevisionValidationError> errors)
    {
        if (GovernedLoopRevisionContractGuard.HasConflictingRevisionContent(left, right))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.InvalidLineage, path, "One immutable revision identifier cannot bind different executable content hashes.");
        }
    }

    private static void ValidateReferenceGraph(GovernedLoopRevisionReference? revision, string graphId, string path, List<GovernedLoopRevisionValidationError> errors)
    {
        if (revision is not null && !string.Equals(revision.GraphId, graphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionValidationErrorCode.GraphMismatch, path, "Related evidence must identify one exact graph.");
        }
    }

    private static void AddNested(List<GovernedLoopRevisionValidationError> errors, GovernedLoopRevisionValidationResult nested, string prefix)
    {
        foreach (var error in nested.Errors)
        {
            var suffix = error.Path == "$" ? string.Empty : error.Path[1..];
            Add(errors, error.Code, $"{prefix}{suffix}", error.Message);
        }
    }

    private static GovernedLoopRevisionValidationResult Result(IEnumerable<GovernedLoopRevisionValidationError> errors)
        => GovernedLoopRevisionValidationResult.FromErrors(errors);

    private static void Add(List<GovernedLoopRevisionValidationError> errors, GovernedLoopRevisionValidationErrorCode code, string path, string message)
    {
        if (errors.Count >= GovernedLoopRevisionContractLimits.MaxValidationErrors)
        {
            return;
        }

        var safePath = path.Length is > 0 and <= GovernedLoopRevisionContractLimits.MaxErrorPathCharacters ? path : "$";
        errors.Add(new GovernedLoopRevisionValidationError(code, safePath, message));
    }
}
