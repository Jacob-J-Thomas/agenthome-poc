using System.Collections.Immutable;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates strict schema-1 Human Review contracts without admitting work, authorizing actors, persisting state, waking loops, or dispatching effects.</summary>
public static class HumanReviewContractValidator
{
    /// <summary>Validates one immutable review request, its exact approval scope, bounded previews, provenance, and canonical hash.</summary>
    /// <param name="request">The untrusted request candidate.</param>
    /// <returns>All deterministic request-boundary errors.</returns>
    public static HumanReviewContractValidationResult ValidateRequest(HumanReviewRequest? request)
    {
        var errors = new List<HumanReviewContractValidationError>();
        if (request is null)
        {
            Add(errors, "request_required", "$", "A Human Review request is required.");
            return Result(errors);
        }

        ValidateSchema(request.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(request.RequestId, "$.requestId", errors);
        ValidateIdentifier(request.RequestOperationId, "$.requestOperationId", errors);
        if (string.Equals(request.RequestId, request.RequestOperationId, StringComparison.Ordinal))
        {
            Add(errors, "request_identity_collision", "$.requestOperationId", "Request and request-operation identities must be distinct.");
        }

        ValidateBinding(request.Binding, "$.binding", errors);
        if (!Enum.IsDefined(request.Purpose) || request.Purpose == HumanReviewPurpose.Unknown)
        {
            Add(errors, "unsupported_purpose", "$.purpose", "A supported Human Review purpose is required.");
        }

        ValidateRequestedDecisions(request.Purpose, request.RequestedDecisions, errors);
        ValidateReviewerScopes(request.EligibleReviewers, "$.eligibleReviewers", errors);
        ValidateApprovalScope(request, errors);
        ValidatePreviews(request.Previews, "$.previews", requireCompleteReviewPreview: true, errors);
        ValidateTiming(request.Timing, errors);
        ValidateProvenance(request.Provenance, "$.provenance", HumanReviewProvenanceKind.Server, errors);
        if (request.Timing is not null && request.Provenance is not null && request.Provenance.ObservedAtUtc != request.Timing.CreatedAtUtc)
        {
            Add(errors, "request_provenance_time_mismatch", "$.provenance.observedAtUtc", "Request provenance observation time must exactly match request creation time.");
        }

        ValidateHash(request.RequestHash, "$.requestHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesRequest(request))
        {
            Add(errors, "request_hash_mismatch", "$.requestHash", "Request hash must match the complete canonical request contract.");
        }

        return Result(errors);
    }

    /// <summary>Validates one authenticated decision against one exact immutable review request without accepting, replaying, or releasing it.</summary>
    /// <param name="request">The exact request that defines the only allowed decision vocabulary and reviewer scope.</param>
    /// <param name="decision">The untrusted decision candidate.</param>
    /// <returns>All deterministic request-relative decision-boundary errors.</returns>
    public static HumanReviewContractValidationResult ValidateDecision(HumanReviewRequest? request, HumanReviewDecision? decision)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsValid)
        {
            return requestValidation;
        }

        var errors = new List<HumanReviewContractValidationError>();
        if (decision is null)
        {
            Add(errors, "decision_required", "$", "A Human Review decision is required.");
            return Result(errors);
        }

        if (request is null)
        {
            return Result(errors);
        }

        ValidateSchema(decision.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(decision.DecisionId, "$.decisionId", errors);
        ValidateIdentifier(decision.DecisionOperationId, "$.decisionOperationId", errors);
        if (string.Equals(decision.DecisionId, decision.DecisionOperationId, StringComparison.Ordinal))
        {
            Add(errors, "decision_identity_collision", "$.decisionOperationId", "Decision and decision-operation identities must be distinct.");
        }

        ValidateRequestReference(decision.Request, "$.request", errors);
        if (decision.Request is not null && (!string.Equals(decision.Request.RequestId, request.RequestId, StringComparison.Ordinal) || !string.Equals(decision.Request.RequestHash, request.RequestHash, StringComparison.Ordinal)))
        {
            Add(errors, "request_reference_mismatch", "$.request", "Decision request reference must exactly match the immutable reviewed request.");
        }

        if (!IsSupportedDecisionForPurpose(request.Purpose, decision.Kind))
        {
            Add(errors, "unsupported_decision_purpose", "$.kind", "The decision kind is unsupported for the request purpose.");
        }
        else if (request.RequestedDecisions.IsDefault || !request.RequestedDecisions.Contains(decision.Kind))
        {
            Add(errors, "decision_not_requested", "$.kind", "The decision kind was not offered by the immutable review request.");
        }

        ValidateIdentifier(decision.AuthenticatedActorId, "$.authenticatedActorId", errors);
        ValidateIdentifier(decision.ReviewerRoleId, "$.reviewerRoleId", errors);
        ValidateOrderedIdentifiers(decision.ReviewerScopeIds, "$.reviewerScopeIds", errors);
        ValidateDecisionReviewerEligibility(request, decision, errors);
        if (!IsUtc(decision.DecidedAtUtc) || decision.DecidedAtUtc < request.Timing.CreatedAtUtc || decision.DecidedAtUtc > request.Timing.ExpiresAtUtc)
        {
            Add(errors, "decision_outside_window", "$.decidedAtUtc", "Decision time must be trusted UTC and inside the inclusive request creation and expiry window.");
        }

        if (!HumanReviewSafeText.IsValid(decision.Detail, HumanReviewContractLimits.MaxDecisionDetailCharacters, required: decision.Kind == HumanReviewDecisionKind.RequestInformation))
        {
            Add(errors, "invalid_decision_detail", "$.detail", "Decision detail must be bounded, canonical, redacted display-safe text and is required only for RequestInformation.");
        }

        ValidateProvenance(decision.Provenance, "$.provenance", HumanReviewProvenanceKind.AuthenticatedReviewer, errors);
        if (decision.Provenance is not null && (!string.Equals(decision.Provenance.SourceId, decision.AuthenticatedActorId, StringComparison.Ordinal) || decision.Provenance.ObservedAtUtc != decision.DecidedAtUtc))
        {
            Add(errors, "decision_provenance_mismatch", "$.provenance", "Decision provenance must name the authenticated actor and exact decision time.");
        }

        ValidateHash(decision.DecisionHash, "$.decisionHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesDecision(decision))
        {
            Add(errors, "decision_hash_mismatch", "$.decisionHash", "Decision hash must match the complete canonical decision contract.");
        }

        return Result(errors);
    }

    /// <summary>Validates one exact optimistic lifecycle head against its immutable request without resolving conflicts or releasing work.</summary>
    /// <param name="request">The exact immutable review request.</param>
    /// <param name="lifecycle">The lifecycle head candidate.</param>
    /// <returns>All deterministic lifecycle-boundary errors.</returns>
    public static HumanReviewContractValidationResult ValidateLifecycle(HumanReviewRequest? request, HumanReviewLifecycle? lifecycle)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsValid)
        {
            return requestValidation;
        }

        var errors = new List<HumanReviewContractValidationError>();
        if (lifecycle is null)
        {
            Add(errors, "lifecycle_required", "$", "A Human Review lifecycle head is required.");
            return Result(errors);
        }

        if (request is null)
        {
            return Result(errors);
        }

        ValidateSchema(lifecycle.SchemaVersion, "$.schemaVersion", errors);
        ValidateRequestReference(lifecycle.Request, "$.request", errors);
        ValidateExactRequestReference(request, lifecycle.Request, "$.request", errors);
        if (!Enum.IsDefined(lifecycle.Status) || lifecycle.Status == HumanReviewLifecycleStatus.Unknown)
        {
            Add(errors, "unsupported_lifecycle_status", "$.status", "A supported Human Review lifecycle status is required.");
        }

        if (lifecycle.LifecycleVersion is < 1 or > HumanReviewContractLimits.MaxVersion)
        {
            Add(errors, "invalid_lifecycle_version", "$.lifecycleVersion", "Lifecycle version must be positive and within schema-1 bounds.");
        }

        ValidateLifecycleTiming(request, lifecycle, errors);
        ValidateDecisionReference(lifecycle.LastDecision, "$.lastDecision", required: false, errors);
        ValidateLifecycleDecision(lifecycle.Status, lifecycle.LastDecision, errors);
        ValidateProvenance(lifecycle.Provenance, "$.provenance", HumanReviewProvenanceKind.Server, HumanReviewProvenanceKind.Coordinator, errors);
        if (lifecycle.Provenance is not null && lifecycle.Provenance.ObservedAtUtc != lifecycle.UpdatedAtUtc)
        {
            Add(errors, "lifecycle_provenance_time_mismatch", "$.provenance.observedAtUtc", "Lifecycle provenance observation time must exactly match lifecycle update time.");
        }

        ValidateOptionalHash(lifecycle.PreviousLifecycleHash, "$.previousLifecycleHash", errors);
        ValidateHash(lifecycle.LifecycleHash, "$.lifecycleHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesLifecycle(lifecycle))
        {
            Add(errors, "lifecycle_hash_mismatch", "$.lifecycleHash", "Lifecycle hash must match the complete canonical lifecycle contract.");
        }

        return Result(errors);
    }

    /// <summary>Validates one append-only evidence artifact against its immutable request without appending, publishing, waking, or dispatching work.</summary>
    /// <param name="request">The exact immutable review request.</param>
    /// <param name="evidence">The evidence candidate.</param>
    /// <returns>All deterministic evidence-boundary errors.</returns>
    public static HumanReviewContractValidationResult ValidateEvidence(HumanReviewRequest? request, HumanReviewEvidence? evidence)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsValid)
        {
            return requestValidation;
        }

        var errors = new List<HumanReviewContractValidationError>();
        if (evidence is null)
        {
            Add(errors, "evidence_required", "$", "A Human Review evidence artifact is required.");
            return Result(errors);
        }

        if (request is null)
        {
            return Result(errors);
        }

        ValidateSchema(evidence.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(evidence.EvidenceId, "$.evidenceId", errors);
        ValidateRequestReference(evidence.Request, "$.request", errors);
        ValidateExactRequestReference(request, evidence.Request, "$.request", errors);
        if (!Enum.IsDefined(evidence.Kind) || evidence.Kind == HumanReviewEvidenceKind.Unknown)
        {
            Add(errors, "unsupported_evidence_kind", "$.kind", "A supported Human Review evidence kind is required.");
        }

        ValidateDecisionReference(evidence.Decision, "$.decision", required: false, errors);
        ValidateEvidenceDecision(evidence.Kind, evidence.Decision, errors);
        if (!IsUtc(evidence.RecordedAtUtc) || evidence.RecordedAtUtc < request.Timing.CreatedAtUtc)
        {
            Add(errors, "invalid_evidence_time", "$.recordedAtUtc", "Evidence time must be trusted UTC and cannot predate request creation.");
        }

        ValidateProvenance(evidence.Provenance, "$.provenance", HumanReviewProvenanceKind.Server, HumanReviewProvenanceKind.Coordinator, errors);
        if (evidence.Provenance is not null && evidence.Provenance.ObservedAtUtc != evidence.RecordedAtUtc)
        {
            Add(errors, "evidence_provenance_time_mismatch", "$.provenance.observedAtUtc", "Evidence provenance observation time must exactly match evidence recording time.");
        }

        ValidatePreviews(evidence.Previews, "$.previews", requireCompleteReviewPreview: false, errors);
        ValidateOptionalHash(evidence.PreviousEvidenceHash, "$.previousEvidenceHash", errors);
        ValidateHash(evidence.EvidenceHash, "$.evidenceHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesEvidence(evidence))
        {
            Add(errors, "evidence_hash_mismatch", "$.evidenceHash", "Evidence hash must match the complete canonical append-only evidence contract.");
        }

        return Result(errors);
    }

    private static void ValidateBinding(HumanReviewBinding? binding, string path, List<HumanReviewContractValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, "binding_required", path, "An exact Human Review binding is required.");
            return;
        }

        ValidateSchema(binding.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateWorkspaceId(binding.WorkspaceId, $"{path}.workspaceId", errors);
        ValidateIdentifier(binding.RunId, $"{path}.runId", errors);
        ValidateIdentifier(binding.GraphId, $"{path}.graphId", errors);
        ValidateIdentifier(binding.RevisionId, $"{path}.revisionId", errors);
        ValidateHash(binding.RevisionHash, $"{path}.revisionHash", errors);
        ValidateIdentifier(binding.NodeId, $"{path}.nodeId", errors);
        if (binding.ActivationOrdinal is { } activation && activation is < 0 or > HumanReviewContractLimits.MaxActivationOrVisit)
        {
            Add(errors, "invalid_activation_ordinal", $"{path}.activationOrdinal", "Activation ordinal must be zero-based and within schema-1 bounds.");
        }

        if (binding.VisitOrdinal is { } visit && visit is < 1 or > HumanReviewContractLimits.MaxActivationOrVisit)
        {
            Add(errors, "invalid_visit_ordinal", $"{path}.visitOrdinal", "Visit ordinal must be positive and within schema-1 bounds.");
        }

        if ((binding.ActivationOrdinal is null) == (binding.VisitOrdinal is null))
        {
            Add(errors, "ambiguous_activation_visit", path, "Exactly one activation ordinal or visit ordinal must bind the request.");
        }

        if (binding.Attempt is < 1 or > HumanReviewContractLimits.MaxNodeAttempt)
        {
            Add(errors, "invalid_node_attempt", $"{path}.attempt", "Node attempt must be positive and within schema-1 bounds.");
        }

        ValidateIdentifier(binding.FrontierId, $"{path}.frontierId", errors);
        if (binding.FrontierVersion is < 1 or > HumanReviewContractLimits.MaxVersion)
        {
            Add(errors, "invalid_frontier_version", $"{path}.frontierVersion", "Frontier version must be positive and within schema-1 bounds.");
        }

        ValidateHash(binding.FrontierHash, $"{path}.frontierHash", errors);
        ValidateHash(binding.AuthorityProfileHash, $"{path}.authorityProfileHash", errors);
        ValidateHash(binding.AuthorityGrantHash, $"{path}.authorityGrantHash", errors);
        ValidateHash(binding.CapabilityHash, $"{path}.capabilityHash", errors);
        ValidateHash(binding.ModelProfileHash, $"{path}.modelProfileHash", errors);
        ValidateHash(binding.TargetHash, $"{path}.targetHash", errors);
        ValidateHash(binding.PreconditionHash, $"{path}.preconditionHash", errors);
        ValidateHash(binding.PayloadHash, $"{path}.payloadHash", errors);
        ValidateEffectAttempt(binding.EffectAttempt, $"{path}.effectAttempt", errors);
        ValidateHash(binding.BindingHash, $"{path}.bindingHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesBinding(binding))
        {
            Add(errors, "binding_hash_mismatch", $"{path}.bindingHash", "Binding hash must match the complete canonical binding contract.");
        }
    }

    private static void ValidateWorkspaceId(string? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!ContextualRoleWorkspaceId.IsValid(value))
        {
            Add(errors, "invalid_workspace_id", path, "Workspace identifiers must use the canonical workspace-sha256 scope form.");
        }
    }

    private static void ValidateEffectAttempt(HumanReviewEffectAttemptBinding? effectAttempt, string path, List<HumanReviewContractValidationError> errors)
    {
        if (effectAttempt is null)
        {
            return;
        }

        ValidateIdentifier(effectAttempt.EffectAttemptId, $"{path}.effectAttemptId", errors);
        ValidateIdentifier(effectAttempt.OperationId, $"{path}.operationId", errors);
        if (effectAttempt.EffectGeneration is < 1 or > HumanReviewContractLimits.MaxVersion)
        {
            Add(errors, "invalid_effect_generation", $"{path}.effectGeneration", "Effect generation must be positive and within schema-1 bounds.");
        }

        ValidateHash(effectAttempt.IntentHash, $"{path}.intentHash", errors);
        ValidateHash(effectAttempt.PreparationHash, $"{path}.preparationHash", errors);
        if (effectAttempt.DispatchCertainty != HumanReviewEffectDispatchCertainty.NotDispatched)
        {
            Add(errors, "effect_not_conclusively_pre_dispatch", $"{path}.dispatchCertainty", "Human Review can bind an effect only when evidence conclusively proves it has not dispatched.");
        }

        ValidateHash(effectAttempt.EffectAttemptHash, $"{path}.effectAttemptHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesEffectAttempt(effectAttempt))
        {
            Add(errors, "effect_attempt_hash_mismatch", $"{path}.effectAttemptHash", "Effect-attempt hash must match its complete canonical binding.");
        }
    }

    private static void ValidateRequestedDecisions(HumanReviewPurpose purpose, ImmutableArray<HumanReviewDecisionKind> decisions, List<HumanReviewContractValidationError> errors)
    {
        if (decisions.IsDefault || decisions.Length is < 1 or > HumanReviewContractLimits.MaxRequestedDecisions)
        {
            Add(errors, "invalid_requested_decision_count", "$.requestedDecisions", "At least one and no more than four requested decision kinds are required.");
            return;
        }

        var previous = HumanReviewDecisionKind.Unknown;
        for (var index = 0; index < decisions.Length; index++)
        {
            var kind = decisions[index];
            if (!IsSupportedDecisionForPurpose(purpose, kind))
            {
                Add(errors, "unsupported_decision_purpose", $"$.requestedDecisions[{index}]", "Requested decision is unsupported for the review purpose.");
            }

            if (index > 0 && kind <= previous)
            {
                Add(errors, "noncanonical_requested_decisions", $"$.requestedDecisions[{index}]", "Requested decisions must be unique and ordered by closed vocabulary value.");
            }

            previous = kind;
        }
    }

    private static void ValidateReviewerScopes(ImmutableArray<HumanReviewReviewerScope> reviewers, string path, List<HumanReviewContractValidationError> errors)
    {
        if (reviewers.IsDefault || reviewers.Length is < 1 or > HumanReviewContractLimits.MaxEligibleReviewers)
        {
            Add(errors, "invalid_reviewer_count", path, "At least one and no more than sixteen exact eligible reviewer role entries are required.");
            return;
        }

        string? previousRole = null;
        for (var index = 0; index < reviewers.Length; index++)
        {
            var reviewer = reviewers[index];
            var itemPath = $"{path}[{index}]";
            if (reviewer is null)
            {
                Add(errors, "reviewer_required", itemPath, "Eligible reviewer entries cannot be null.");
                continue;
            }

            ValidateIdentifier(reviewer.ReviewerRoleId, $"{itemPath}.reviewerRoleId", errors);
            if (previousRole is not null && string.CompareOrdinal(previousRole, reviewer.ReviewerRoleId) >= 0)
            {
                Add(errors, "noncanonical_reviewer_order", $"{itemPath}.reviewerRoleId", "Eligible reviewer roles must be unique and ordered ordinally.");
            }

            previousRole = reviewer.ReviewerRoleId;
            ValidateOrderedIdentifiers(reviewer.ScopeIds, $"{itemPath}.scopeIds", errors);
        }
    }

    private static void ValidateApprovalScope(HumanReviewRequest request, List<HumanReviewContractValidationError> errors)
    {
        var scope = request.ApprovalScope;
        if (scope is null)
        {
            Add(errors, "approval_scope_required", "$.approvalScope", "One exact approval scope is required.");
            return;
        }

        if (!Enum.IsDefined(scope.Kind) || scope.Kind == HumanReviewApprovalScopeKind.Unknown)
        {
            Add(errors, "unsupported_approval_scope", "$.approvalScope.kind", "A supported exact approval scope is required.");
        }

        ValidateHash(scope.BindingHash, "$.approvalScope.bindingHash", errors);
        ValidateOptionalIdentifier(scope.EffectAttemptId, "$.approvalScope.effectAttemptId", errors);
        ValidateHash(scope.ScopeHash, "$.approvalScope.scopeHash", errors);
        if (scope.Kind == HumanReviewApprovalScopeKind.Continuation)
        {
            if (request.Purpose != HumanReviewPurpose.Continuation || request.Binding?.EffectAttempt is not null || scope.EffectAttemptId is not null)
            {
                Add(errors, "continuation_scope_mismatch", "$.approvalScope", "Continuation approval scope requires a continuation purpose with no effect attempt binding.");
            }
        }
        else if (scope.Kind == HumanReviewApprovalScopeKind.PreDispatchEffect)
        {
            if (request.Purpose != HumanReviewPurpose.PreDispatchEffect || request.Binding?.EffectAttempt is null || !string.Equals(scope.EffectAttemptId, request.Binding.EffectAttempt.EffectAttemptId, StringComparison.Ordinal))
            {
                Add(errors, "effect_scope_mismatch", "$.approvalScope", "Pre-dispatch effect approval scope must name the exact bound not-yet-dispatched effect attempt.");
            }
        }

        if (request.Binding is not null && !string.Equals(scope.BindingHash, request.Binding.BindingHash, StringComparison.Ordinal))
        {
            Add(errors, "approval_scope_binding_mismatch", "$.approvalScope.bindingHash", "Approval scope must bind the exact immutable request binding hash.");
        }

        if (errors.Count == 0 && !HumanReviewContractHash.MatchesApprovalScope(scope))
        {
            Add(errors, "approval_scope_hash_mismatch", "$.approvalScope.scopeHash", "Approval scope hash must match the exact canonical scope.");
        }
    }

    private static void ValidatePreviews(ImmutableArray<HumanReviewRedactedPreview> previews, string path, bool requireCompleteReviewPreview, List<HumanReviewContractValidationError> errors)
    {
        var minimum = requireCompleteReviewPreview ? 3 : 0;
        if (previews.IsDefault || previews.Length < minimum || previews.Length > HumanReviewContractLimits.MaxPreviews)
        {
            Add(errors, "invalid_preview_count", path, requireCompleteReviewPreview ? "Exactly the required bounded action, result, and evidence previews are required." : "Evidence previews must remain within schema-1 bounds.");
            return;
        }

        HumanReviewRedactedPreview? previous = null;
        var kinds = new HashSet<HumanReviewPreviewKind>();
        for (var index = 0; index < previews.Length; index++)
        {
            var preview = previews[index];
            var itemPath = $"{path}[{index}]";
            if (preview is null)
            {
                Add(errors, "preview_required", itemPath, "Preview entries cannot be null.");
                continue;
            }

            if (!Enum.IsDefined(preview.Kind) || preview.Kind == HumanReviewPreviewKind.Unknown)
            {
                Add(errors, "unsupported_preview_kind", $"{itemPath}.kind", "A supported preview kind is required.");
            }

            if (!kinds.Add(preview.Kind))
            {
                Add(errors, "duplicate_preview_kind", $"{itemPath}.kind", "Each retained preview kind may appear at most once.");
            }

            if (!HumanReviewSafeText.IsValid(preview.Label, HumanReviewContractLimits.MaxPreviewLabelCharacters, required: true) || !HumanReviewSafeText.IsValid(preview.Detail, HumanReviewContractLimits.MaxPreviewDetailCharacters, required: true))
            {
                Add(errors, "unsafe_preview", itemPath, "Previews must be bounded canonical redacted display-safe text without secret-bearing material.");
            }

            ValidateHash(preview.DetailHash, $"{itemPath}.detailHash", errors);
            if (previous is not null && ComparePreview(previous, preview) >= 0)
            {
                Add(errors, "noncanonical_preview_order", itemPath, "Previews must be uniquely ordered by kind, label, then canonical detail hash.");
            }

            previous = preview;
            if (errors.Count == 0 && !HumanReviewContractHash.MatchesPreview(preview))
            {
                Add(errors, "preview_hash_mismatch", $"{itemPath}.detailHash", "Preview hash must match the exact canonical redacted preview.");
            }
        }

        if (requireCompleteReviewPreview && !kinds.SetEquals([HumanReviewPreviewKind.Action, HumanReviewPreviewKind.Result, HumanReviewPreviewKind.Evidence]))
        {
            Add(errors, "incomplete_review_previews", path, "A review request requires exactly one canonical Action, Result, and Evidence preview.");
        }
    }

    private static void ValidateTiming(HumanReviewTiming? timing, List<HumanReviewContractValidationError> errors)
    {
        if (timing is null || !IsUtc(timing.CreatedAtUtc) || !IsUtc(timing.DueAtUtc) || !IsUtc(timing.ExpiresAtUtc))
        {
            Add(errors, "invalid_timing", "$.timing", "Creation, due, and expiry timestamps must be non-default UTC values.");
            return;
        }

        if (timing.CreatedAtUtc > timing.DueAtUtc || timing.DueAtUtc > timing.ExpiresAtUtc || timing.ExpiresAtUtc - timing.CreatedAtUtc > HumanReviewContractLimits.MaxReviewWindow)
        {
            Add(errors, "inconsistent_timing", "$.timing", "Timing must satisfy created <= due <= expiry within the finite schema-1 review window.");
        }
    }

    private static void ValidateProvenance(HumanReviewProvenance? provenance, string path, HumanReviewProvenanceKind expected, List<HumanReviewContractValidationError> errors)
        => ValidateProvenance(provenance, path, [expected], errors);

    private static void ValidateProvenance(HumanReviewProvenance? provenance, string path, HumanReviewProvenanceKind first, HumanReviewProvenanceKind second, List<HumanReviewContractValidationError> errors)
        => ValidateProvenance(provenance, path, [first, second], errors);

    private static void ValidateProvenance(HumanReviewProvenance? provenance, string path, HumanReviewProvenanceKind[] allowedKinds, List<HumanReviewContractValidationError> errors)
    {
        if (provenance is null)
        {
            Add(errors, "provenance_required", path, "Immutable trusted provenance is required.");
            return;
        }

        if (!Enum.IsDefined(provenance.Kind) || !allowedKinds.Contains(provenance.Kind))
        {
            Add(errors, "unsupported_provenance_kind", $"{path}.kind", "Provenance source is unsupported for this Human Review artifact.");
        }

        ValidateIdentifier(provenance.SourceId, $"{path}.sourceId", errors);
        ValidateIdentifier(provenance.CorrelationId, $"{path}.correlationId", errors);
        if (!IsUtc(provenance.ObservedAtUtc))
        {
            Add(errors, "invalid_provenance_time", $"{path}.observedAtUtc", "Provenance observation time must be non-default UTC.");
        }

        ValidateHash(provenance.ProvenanceHash, $"{path}.provenanceHash", errors);
        if (errors.Count == 0 && !HumanReviewContractHash.MatchesProvenance(provenance))
        {
            Add(errors, "provenance_hash_mismatch", $"{path}.provenanceHash", "Provenance hash must match the exact canonical provenance.");
        }
    }

    private static void ValidateDecisionReviewerEligibility(HumanReviewRequest request, HumanReviewDecision decision, List<HumanReviewContractValidationError> errors)
    {
        if (request.EligibleReviewers.IsDefault || decision.ReviewerScopeIds.IsDefault)
        {
            return;
        }

        var reviewer = request.EligibleReviewers.SingleOrDefault(candidate => candidate is not null && string.Equals(candidate.ReviewerRoleId, decision.ReviewerRoleId, StringComparison.Ordinal));
        if (reviewer is null || !reviewer.ScopeIds.SequenceEqual(decision.ReviewerScopeIds, StringComparer.Ordinal))
        {
            Add(errors, "ineligible_reviewer_scope", "$.reviewerScopeIds", "Decision reviewer role and exact canonical scope set must be one immutable request eligibility entry.");
        }
    }

    private static void ValidateLifecycleTiming(HumanReviewRequest request, HumanReviewLifecycle lifecycle, List<HumanReviewContractValidationError> errors)
    {
        if (!IsUtc(lifecycle.UpdatedAtUtc) || lifecycle.UpdatedAtUtc < request.Timing.CreatedAtUtc)
        {
            Add(errors, "invalid_lifecycle_time", "$.updatedAtUtc", "Lifecycle update time must be trusted UTC and cannot predate request creation.");
            return;
        }

        if (lifecycle.Status == HumanReviewLifecycleStatus.Expired)
        {
            if (lifecycle.UpdatedAtUtc < request.Timing.ExpiresAtUtc)
            {
                Add(errors, "early_expiry", "$.updatedAtUtc", "Expired lifecycle status cannot predate the inclusive expiry boundary.");
            }
        }
        else if (lifecycle.UpdatedAtUtc > request.Timing.ExpiresAtUtc)
        {
            Add(errors, "lifecycle_after_expiry", "$.updatedAtUtc", "Non-expired lifecycle status cannot advance after the inclusive expiry boundary.");
        }
    }

    private static void ValidateLifecycleDecision(HumanReviewLifecycleStatus status, HumanReviewDecisionReference? decision, List<HumanReviewContractValidationError> errors)
    {
        var expected = status switch
        {
            HumanReviewLifecycleStatus.AwaitingInformation => HumanReviewDecisionKind.RequestInformation,
            HumanReviewLifecycleStatus.Approved => HumanReviewDecisionKind.Approve,
            HumanReviewLifecycleStatus.Rejected => HumanReviewDecisionKind.Reject,
            HumanReviewLifecycleStatus.Cancelled => HumanReviewDecisionKind.Cancel,
            _ => HumanReviewDecisionKind.Unknown
        };
        if (expected == HumanReviewDecisionKind.Unknown && decision is not null)
        {
            Add(errors, "unexpected_lifecycle_decision", "$.lastDecision", "This lifecycle status cannot retain an accepted decision reference.");
        }
        else if (expected != HumanReviewDecisionKind.Unknown && (decision is null || decision.Kind != expected))
        {
            Add(errors, "lifecycle_decision_mismatch", "$.lastDecision", "Lifecycle status must retain the exact matching accepted decision kind.");
        }
    }

    private static void ValidateEvidenceDecision(HumanReviewEvidenceKind kind, HumanReviewDecisionReference? decision, List<HumanReviewContractValidationError> errors)
    {
        var expected = kind switch
        {
            HumanReviewEvidenceKind.DecisionAttempted => (HumanReviewDecisionKind?)null,
            HumanReviewEvidenceKind.DecisionAccepted => (HumanReviewDecisionKind?)null,
            HumanReviewEvidenceKind.InformationRequested => HumanReviewDecisionKind.RequestInformation,
            HumanReviewEvidenceKind.DecisionConflict => (HumanReviewDecisionKind?)null,
            HumanReviewEvidenceKind.ContinuationReserved => HumanReviewDecisionKind.Approve,
            HumanReviewEvidenceKind.ContinuationCompleted => HumanReviewDecisionKind.Approve,
            HumanReviewEvidenceKind.PreDispatchBlocked => HumanReviewDecisionKind.Approve,
            _ => HumanReviewDecisionKind.Unknown
        };
        if (expected == HumanReviewDecisionKind.Unknown)
        {
            if (decision is not null)
            {
                Add(errors, "unexpected_evidence_decision", "$.decision", "This evidence kind cannot retain a decision reference.");
            }

            return;
        }

        if (expected is null && (decision is null || !IsSupportedEvidenceDecision(kind, decision.Kind)))
        {
            Add(errors, "evidence_decision_mismatch", "$.decision", "Evidence kind requires one exact permitted closed decision reference.");
        }
        else if (expected is { } expectedKind && (decision is null || decision.Kind != expectedKind))
        {
            Add(errors, "evidence_decision_mismatch", "$.decision", "Evidence kind must retain the exact matching decision kind.");
        }
    }

    private static bool IsSupportedEvidenceDecision(HumanReviewEvidenceKind kind, HumanReviewDecisionKind decision)
        => kind switch
        {
            HumanReviewEvidenceKind.DecisionAttempted or HumanReviewEvidenceKind.DecisionConflict => decision is HumanReviewDecisionKind.Approve or HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation,
            HumanReviewEvidenceKind.DecisionAccepted => decision is HumanReviewDecisionKind.Approve or HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel,
            _ => false
        };

    private static void ValidateRequestReference(HumanReviewRequestReference? reference, string path, List<HumanReviewContractValidationError> errors)
    {
        if (reference is null)
        {
            Add(errors, "request_reference_required", path, "An exact request reference is required.");
            return;
        }

        ValidateIdentifier(reference.RequestId, $"{path}.requestId", errors);
        ValidateHash(reference.RequestHash, $"{path}.requestHash", errors);
    }

    private static void ValidateExactRequestReference(HumanReviewRequest request, HumanReviewRequestReference? reference, string path, List<HumanReviewContractValidationError> errors)
    {
        if (reference is not null && (!string.Equals(reference.RequestId, request.RequestId, StringComparison.Ordinal) || !string.Equals(reference.RequestHash, request.RequestHash, StringComparison.Ordinal)))
        {
            Add(errors, "request_reference_mismatch", path, "Reference must exactly match the immutable Human Review request identity and hash.");
        }
    }

    private static void ValidateDecisionReference(HumanReviewDecisionReference? reference, string path, bool required, List<HumanReviewContractValidationError> errors)
    {
        if (reference is null)
        {
            if (required)
            {
                Add(errors, "decision_reference_required", path, "An exact decision reference is required.");
            }

            return;
        }

        ValidateIdentifier(reference.DecisionId, $"{path}.decisionId", errors);
        ValidateIdentifier(reference.DecisionOperationId, $"{path}.decisionOperationId", errors);
        if (string.Equals(reference.DecisionId, reference.DecisionOperationId, StringComparison.Ordinal))
        {
            Add(errors, "decision_reference_identity_collision", $"{path}.decisionOperationId", "Decision and decision-operation identities must be distinct.");
        }

        if (!Enum.IsDefined(reference.Kind) || reference.Kind == HumanReviewDecisionKind.Unknown)
        {
            Add(errors, "unsupported_decision_reference_kind", $"{path}.kind", "A supported closed decision kind is required.");
        }

        ValidateHash(reference.DecisionHash, $"{path}.decisionHash", errors);
    }

    private static void ValidateOrderedIdentifiers(ImmutableArray<string> values, string path, List<HumanReviewContractValidationError> errors)
    {
        if (values.IsDefault || values.Length is < 1 or > HumanReviewContractLimits.MaxScopesPerReviewer)
        {
            Add(errors, "invalid_scope_count", path, "At least one and no more than sixteen exact scope identifiers are required.");
            return;
        }

        string? previous = null;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            ValidateIdentifier(value, $"{path}[{index}]", errors);
            if (previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                Add(errors, "noncanonical_scope_order", $"{path}[{index}]", "Scope identifiers must be unique and ordered ordinally.");
            }

            previous = value;
        }
    }

    private static bool IsSupportedDecisionForPurpose(HumanReviewPurpose purpose, HumanReviewDecisionKind kind)
        => purpose is HumanReviewPurpose.Continuation or HumanReviewPurpose.PreDispatchEffect
            && kind is HumanReviewDecisionKind.Approve or HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation;

    private static int ComparePreview(HumanReviewRedactedPreview left, HumanReviewRedactedPreview right)
    {
        var kind = left.Kind.CompareTo(right.Kind);
        if (kind != 0)
        {
            return kind;
        }

        var label = string.CompareOrdinal(left.Label, right.Label);
        return label != 0 ? label : string.CompareOrdinal(left.DetailHash, right.DetailHash);
    }

    private static void ValidateSchema(int value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value != HumanReviewContractLimits.CurrentSchemaVersion)
        {
            Add(errors, "unsupported_schema_version", path, "Human Review schema version must be 1.");
        }
    }

    private static void ValidateIdentifier(string? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!HumanReviewIdentifier.IsValid(value))
        {
            Add(errors, "invalid_identifier", path, "Identifiers must be bounded canonical lowercase ASCII tokens.");
        }
    }

    private static void ValidateOptionalIdentifier(string? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value is not null)
        {
            ValidateIdentifier(value, path, errors);
        }
    }

    private static void ValidateHash(string? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!HumanReviewContractHash.IsSha256(value))
        {
            Add(errors, "invalid_hash", path, "Hash must be a canonical lowercase SHA-256 digest.");
        }
    }

    private static void ValidateOptionalHash(string? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value is not null)
        {
            ValidateHash(value, path, errors);
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static HumanReviewContractValidationResult Result(List<HumanReviewContractValidationError> errors) => new(errors.AsReadOnly());

    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message)
    {
        if (errors.Count < 64)
        {
            errors.Add(new HumanReviewContractValidationError(code, path, message));
        }
    }
}
