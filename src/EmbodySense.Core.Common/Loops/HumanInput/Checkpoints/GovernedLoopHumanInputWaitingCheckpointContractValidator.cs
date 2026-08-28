using System.Collections.Immutable;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Validates immutable, data-only Human Input waiting checkpoints without publishing, storing, notifying, claiming, recovering, resuming, or granting authority.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointContractValidator
{
    private static readonly string[] _authorityTerms = ["approve", "approval", "authorize", "authorization", "grant", "human-review", "human review", "reject"];

    /// <summary>Validates one complete schema-1 waiting checkpoint, including its exact graph binding, request, closed posture, and append-only evidence.</summary>
    /// <param name="checkpoint">The untrusted checkpoint candidate.</param>
    /// <returns>Every deterministic checkpoint contract violation.</returns>
    public static GovernedLoopHumanInputWaitingCheckpointValidationResult Validate(GovernedLoopHumanInputWaitingCheckpoint? checkpoint)
    {
        var errors = new List<GovernedLoopHumanInputWaitingCheckpointValidationError>();
        if (checkpoint is null)
        {
            Add(errors, "checkpoint_required", "$", "A Human Input waiting checkpoint is required.");
            return Result(errors);
        }

        Schema(checkpoint.SchemaVersion, "$.schemaVersion", errors);
        ValidateBinding(checkpoint.Binding, "$.binding", errors);
        ValidateConfiguration(checkpoint.NodeConfiguration, "$.nodeConfiguration", errors);
        ValidateResolvedPolicy(checkpoint.ResolvedPolicy, "$.resolvedPolicy", errors);
        ValidateRequest(checkpoint.Request, "$.request", errors);
        ValidateConfigurationRequestComposition(checkpoint.Binding, checkpoint.NodeConfiguration, checkpoint.ResolvedPolicy, checkpoint.Request, errors);
        ValidatePosture(checkpoint.Posture, "$.posture", errors);
        ValidateEvidence(checkpoint.Evidence, checkpoint.Binding, checkpoint.Request, checkpoint.Posture, errors);
        Hash(checkpoint.CheckpointHash, "$.checkpointHash", errors);
        if (errors.Count == 0 && !GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(checkpoint))
        {
            Add(errors, "checkpoint_hash_mismatch", "$.checkpointHash", "Checkpoint hash must exactly match every immutable coordinate and append-only evidence entry.");
        }

        return Result(errors);
    }

    /// <summary>Validates one standalone append-only checkpoint evidence record without admitting an independent response, effect, or continuation.</summary>
    /// <param name="evidence">The untrusted evidence candidate.</param>
    /// <returns>Every deterministic standalone evidence violation.</returns>
    public static GovernedLoopHumanInputWaitingCheckpointValidationResult ValidateEvidence(GovernedLoopHumanInputWaitingCheckpointEvidence? evidence)
    {
        var errors = new List<GovernedLoopHumanInputWaitingCheckpointValidationError>();
        ValidateEvidenceShape(evidence, "$.evidence", errors);
        return Result(errors);
    }

    private static void ValidateBinding(GovernedLoopHumanInputWaitingCheckpointBinding? binding, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, "binding_required", path, "An exact run, graph, frontier, node, and generation binding is required.");
            return;
        }

        Schema(binding.SchemaVersion, path + ".schemaVersion", errors);
        WorkspaceId(binding.WorkspaceId, path + ".workspaceId", errors);
        Identifier(binding.NodeId, path + ".nodeId", errors);
        Identifier(binding.CheckpointId, path + ".checkpointId", errors);
        Hash(binding.GraphArtifactHash, path + ".graphArtifactHash", errors);
        Hash(binding.GraphLayoutHash, path + ".graphLayoutHash", errors);
        Hash(binding.AdmissionReceiptHash, path + ".admissionReceiptHash", errors);
        Hash(binding.FrontierHash, path + ".frontierHash", errors);
        if (binding.FrontierVersion < 1 || binding.FrontierVersion > GovernedLoopExecutionLimits.MaxVersion)
        {
            Add(errors, "invalid_frontier_version", path + ".frontierVersion", "Frontier version must be positive and within schema-1 bounds.");
        }
        if (binding.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            Add(errors, "invalid_activation_ordinal", path + ".activationOrdinal", "Activation ordinal must be within the bounded frontier range.");
        }
        if (binding.NodeVisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits)
        {
            Add(errors, "invalid_node_visit_ordinal", path + ".nodeVisitOrdinal", "Node visit ordinal must be positive and within schema-1 bounds.");
        }
        if ((binding.CycleId is null) != (binding.CycleIteration is null))
        {
            Add(errors, "invalid_cycle_coordinates", path + ".cycleId", "Cycle identity and iteration must be both present or both absent.");
        }
        if (binding.CycleId is not null)
        {
            Identifier(binding.CycleId, path + ".cycleId", errors);
            if (binding.CycleIteration is < 1 or > GovernedLoopExecutionLimits.MaxCycleIterations)
            {
                Add(errors, "invalid_cycle_iteration", path + ".cycleIteration", "Cycle iteration must be positive and within schema-1 bounds.");
            }
        }

        if (binding.Execution is null || !GovernedLoopExecutionValidator.Validate(binding.Execution).IsValid)
        {
            Add(errors, "invalid_execution_binding", path + ".execution", "A canonical exact execution binding is required.");
        }
        if (binding.Publication is null || !IsPublicationValid(binding.Publication))
        {
            Add(errors, "invalid_publication_pin", path + ".publication", "A canonical exact publication pin is required.");
        }
        if (binding.Execution is not null && binding.Publication is not null && !Equals(binding.Execution.Revision, binding.Publication.Revision))
        {
            Add(errors, "publication_revision_mismatch", path + ".publication.revision", "Publication pin must exactly match the immutable execution revision.");
        }
    }

    private static bool IsPublicationValid(GovernedLoopRevisionPublicationPin publication)
        => publication.SchemaVersion == GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion
            && publication.Revision is not null
            && publication.Revision.SchemaVersion == GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion
            && HumanInputIdentifier.IsValid(publication.Revision.GraphId)
            && HumanInputIdentifier.IsValid(publication.Revision.RevisionId)
            && GovernedLoopHumanInputWaitingCheckpointContractHash.IsSha256(publication.Revision.ExecutableHash)
            && HumanInputIdentifier.IsValid(publication.PublicationOperationId)
            && GovernedLoopHumanInputWaitingCheckpointContractHash.IsSha256(publication.ValidationEvidenceHash);

    private static void ValidateConfiguration(GovernedLoopHumanInputNodeConfiguration? configuration, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!GovernedLoopHumanInputNodeConfigurationValidator.IsValid(configuration))
        {
            Add(errors, "invalid_node_configuration", path, "Checkpoint node configuration must remain the exact safe schema-1 Human Input graph configuration.");
            return;
        }

        if (ContainsAuthorityTerm(configuration!.RequestSchemaReference)
            || ContainsAuthorityTerm(configuration.TimeoutPolicyReference)
            || ContainsAuthorityTerm(configuration.FailurePolicyReference))
        {
            Add(errors, "approval_confused_configuration", path, "Human Input checkpoint configuration must not carry approval, review, authorization, or grant semantics.");
        }
    }

    private static void ValidateRequest(HumanInputRequest? request, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!HumanInputValidator.ValidateRequest(request).IsValid)
        {
            Add(errors, "invalid_request", path, "Checkpoint request must be an exact valid immutable Human Input request.");
        }
    }

    private static void ValidateResolvedPolicy(HumanInputPolicyResolutionSnapshot? policy, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!HumanInputPolicyResolutionSnapshot.IsValid(policy)) Add(errors, "invalid_resolved_policy", path, "A complete exact trusted-time Human Input policy-resolution snapshot is required.");
    }

    private static void ValidateConfigurationRequestComposition(
        GovernedLoopHumanInputWaitingCheckpointBinding? binding,
        GovernedLoopHumanInputNodeConfiguration? configuration,
        HumanInputPolicyResolutionSnapshot? policy,
        HumanInputRequest? request,
        List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (binding is null || configuration is null || policy is null || request is null)
        {
            return;
        }

        if (binding.Execution is null || binding.Execution.Revision is null || request.Binding is null)
        {
            Add(errors, "request_binding_required", "$.request.binding", "Request must retain exact checkpoint binding coordinates.");
            return;
        }

        var requestBinding = request.Binding;
        if (!string.Equals(requestBinding.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(requestBinding.LoopGraphId, binding.Execution.Revision.GraphId, StringComparison.Ordinal)
            || !string.Equals(requestBinding.LoopRevisionId, binding.Execution.Revision.RevisionId, StringComparison.Ordinal)
            || !string.Equals(requestBinding.NodeId, binding.NodeId, StringComparison.Ordinal)
            || !string.Equals(requestBinding.RunId, binding.Execution.RunId, StringComparison.Ordinal)
            || !string.Equals(requestBinding.CheckpointId, binding.CheckpointId, StringComparison.Ordinal))
        {
            Add(errors, "request_binding_mismatch", "$.request.binding", "Request must exactly bind the checkpoint workspace, graph revision, node, run, and checkpoint identity.");
        }
        if (!string.Equals(configuration.Purpose, request.Purpose, StringComparison.Ordinal)
            || !string.Equals(configuration.Prompt, request.Prompt, StringComparison.Ordinal)
            || configuration.PrivacyClass != request.PrivacyClass
            || !ResponseSchemasEqual(configuration.ResponseSchema, request.ResponseSchema)
            || !RespondentsEqual(configuration.EligibleRespondents, request.EligibleRespondents)
            || !ResponsePoliciesEqual(configuration.ResponsePolicy, request.ResponsePolicy))
        {
            Add(errors, "request_configuration_mismatch", "$.request", "Request schema, recipients, privacy, and response policy must exactly match the captured Human Input node configuration.");
        }
        if (request.ContinuationBinding is null
            || request.ContinuationBinding.Kind != HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly
            || !string.Equals(request.ContinuationBinding.NodeId, binding.NodeId, StringComparison.Ordinal)
            || !string.Equals(request.ContinuationBinding.CheckpointId, binding.CheckpointId, StringComparison.Ordinal))
        {
            Add(errors, "continuation_binding_mismatch", "$.request.continuationBinding", "Request continuation visibility must remain bound only to the exact node and checkpoint.");
        }

        if (binding.Execution?.Revision is null
            || !string.Equals(policy.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(policy.GraphId, binding.Execution.Revision.GraphId, StringComparison.Ordinal)
            || !string.Equals(policy.GraphRevisionId, binding.Execution.Revision.RevisionId, StringComparison.Ordinal)
            || !string.Equals(policy.NodeId, binding.NodeId, StringComparison.Ordinal)
            || !HumanInputPolicyReference.TryParse(configuration.TimeoutPolicyReference, out var timeoutReference)
            || !HumanInputPolicyReference.TryParse(configuration.FailurePolicyReference, out var failureReference)
            || !Equals(policy.TimeoutPolicy.Reference, timeoutReference)
            || !Equals(policy.FailurePolicy.Reference, failureReference))
        {
            Add(errors, "resolved_policy_mismatch", "$.resolvedPolicy", "The policy snapshot must exactly bind checkpoint workspace, graph revision, node, and policy references.");
        }

        if (request.Timing is null || request.Timing.RequestedAtUtc != policy.ResolvedAtUtc || request.Timing.ExpiresAtUtc != policy.ExpiresAtUtc)
        {
            Add(errors, "request_policy_timing_mismatch", "$.request.timing", "The request response window must exactly equal the trusted policy snapshot timing.");
        }
    }

    private static bool ResponseSchemasEqual(HumanInputResponseSchema? left, HumanInputResponseSchema? right)
        => left is not null
            && right is not null
            && left.Kind == right.Kind
            && left.MaxTextCharacters == right.MaxTextCharacters
            && ChoicesEqual(left.Choices, right.Choices)
            && StructuredFieldsEqual(left.StructuredFields, right.StructuredFields)
            && Equals(left.ReferencePolicy, right.ReferencePolicy);

    private static bool ChoicesEqual(HumanInputChoice[]? left, HumanInputChoice[]? right)
        => left is null && right is null || left is not null && right is not null && left.Length == right.Length && left.Select((value, index) => Equals(value, right[index])).All(value => value);

    private static bool StructuredFieldsEqual(HumanInputStructuredFieldSchema[]? left, HumanInputStructuredFieldSchema[]? right)
        => left is null && right is null || left is not null && right is not null && left.Length == right.Length && left.Select((value, index) => value is not null && right[index] is not null && value.FieldId == right[index].FieldId && value.Kind == right[index].Kind && value.Required == right[index].Required && value.MaxTextCharacters == right[index].MaxTextCharacters && ChoicesEqual(value.Choices, right[index].Choices)).All(value => value);

    private static bool RespondentsEqual(IReadOnlyList<HumanInputEligibleRespondent?>? configuration, HumanInputEligibleRespondent[]? request)
        => configuration is not null
            && request is not null
            && configuration.Count == request.Length
            && configuration.Select((value, index) => Equals(value, request[index])).All(value => value);

    private static bool ResponsePoliciesEqual(HumanInputResponsePolicy? left, HumanInputResponsePolicy? right)
    {
        if (left is null || right is null || left.Kind != right.Kind || left.RequiredResponseCount != right.RequiredResponseCount)
        {
            return false;
        }

        if (left.OrderedRoleIds is not { } leftRoles || right.OrderedRoleIds is not { } rightRoles)
        {
            return left.OrderedRoleIds is null && right.OrderedRoleIds is null;
        }

        return leftRoles.IsDefault == rightRoles.IsDefault && leftRoles.Length == rightRoles.Length && leftRoles.SequenceEqual(rightRoles, StringComparer.Ordinal);
    }

    private static void ValidatePosture(GovernedLoopHumanInputWaitingCheckpointPosture posture, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!Enum.IsDefined(posture) || posture == GovernedLoopHumanInputWaitingCheckpointPosture.Unknown)
        {
            Add(errors, "unsupported_posture", path, "A closed supported Human Input checkpoint posture is required.");
        }
    }

    private static void ValidateEvidence(
        ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> evidence,
        GovernedLoopHumanInputWaitingCheckpointBinding? binding,
        HumanInputRequest? request,
        GovernedLoopHumanInputWaitingCheckpointPosture posture,
        List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (evidence.IsDefault || evidence.Length is < 1 or > GovernedLoopHumanInputWaitingCheckpointContractLimits.MaxEvidenceEntries)
        {
            Add(errors, "invalid_evidence_count", "$.evidence", "Evidence history must be defined and bounded by the closed checkpoint posture table.");
            return;
        }

        for (var index = 0; index < evidence.Length; index++)
        {
            var item = evidence[index];
            ValidateEvidenceShape(item, $"$.evidence[{index}]", errors);
            if (item is null)
            {
                continue;
            }

            if (item.Sequence != index + 1)
            {
                Add(errors, "noncontiguous_evidence_sequence", $"$.evidence[{index}].sequence", "Evidence sequence must begin at one and advance by exactly one.");
            }
            var expectedPreviousHash = index == 0 ? string.Empty : evidence[index - 1]?.EvidenceHash;
            if (!string.Equals(item.PreviousEvidenceHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                Add(errors, "evidence_chain_mismatch", $"$.evidence[{index}].previousEvidenceHash", "Evidence must retain the exact prior evidence hash or empty sequence-one predecessor.");
            }
            if (index > 0 && evidence[index - 1] is { } previous && item.OccurredAtUtc < previous.OccurredAtUtc)
            {
                Add(errors, "evidence_time_regressed", $"$.evidence[{index}].occurredAtUtc", "Evidence timestamps must be monotonic trusted UTC instants.");
            }
        }

        if (binding is null || request?.Timing is null || evidence[0] is not { } first)
        {
            return;
        }

        if (first.Kind != GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published || first.OccurredAtUtc != request.Timing.RequestedAtUtc)
        {
            Add(errors, "invalid_publication_evidence", "$.evidence[0]", "Sequence-one evidence must publish the checkpoint at the exact request-window start.");
        }
        if (first.AnswerSelection is not null || first.SupersedingCheckpointId is not null || first.SupersedingCheckpointHash is not null || first.TerminalizationReceiptId is not null || first.TerminalizationReceiptHash is not null)
        {
            Add(errors, "publication_evidence_payload", "$.evidence[0]", "Publication evidence must contain no answer, supersession, or terminalization payload.");
        }

        GovernedLoopHumanInputWaitingCheckpointEvidenceKind[] expectedKinds = posture switch
        {
            GovernedLoopHumanInputWaitingCheckpointPosture.Pending => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published],
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered],
            GovernedLoopHumanInputWaitingCheckpointPosture.Expired => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired],
            GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Cancelled],
            GovernedLoopHumanInputWaitingCheckpointPosture.Superseded => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Superseded],
            GovernedLoopHumanInputWaitingCheckpointPosture.Terminal => [GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized],
            _ => [],
        };
        if (evidence.Length != expectedKinds.Length || !evidence.Select((item, index) => item is not null && item.Kind == expectedKinds[index]).All(value => value))
        {
            Add(errors, "posture_evidence_mismatch", "$.evidence", "Evidence history must exactly match the closed posture table.");
            return;
        }

        if (posture is GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed or GovernedLoopHumanInputWaitingCheckpointPosture.Terminal)
        {
            var answer = evidence[1];
            if (answer is null || answer.OccurredAtUtc > request.Timing.ExpiresAtUtc || !SelectionMatchesRequest(answer.AnswerSelection, request))
            {
                Add(errors, "invalid_answer_evidence", "$.evidence[1]", "Answer evidence must be a privacy-safe exact selection for this request inside its response window.");
            }
        }
        if (posture == GovernedLoopHumanInputWaitingCheckpointPosture.Expired && evidence[1] is { } expired && expired.OccurredAtUtc <= request.Timing.ExpiresAtUtc)
        {
            Add(errors, "expired_at_or_before_deadline", "$.evidence[1].occurredAtUtc", "Expired posture may be recorded only strictly after the inclusive response endpoint.");
        }
        if ((posture is GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled or GovernedLoopHumanInputWaitingCheckpointPosture.Superseded) && evidence[1] is { } terminal && terminal.OccurredAtUtc > request.Timing.ExpiresAtUtc)
        {
            Add(errors, "terminal_after_deadline", "$.evidence[1].occurredAtUtc", "Cancellation and supersession must occur before the pending request expires.");
        }
        if (posture == GovernedLoopHumanInputWaitingCheckpointPosture.Superseded
            && evidence[1] is { } supersession
            && string.Equals(supersession.SupersedingCheckpointId, binding.CheckpointId, StringComparison.Ordinal))
        {
            Add(errors, "self_supersession", "$.evidence[1].supersedingCheckpointId", "A superseded checkpoint must name a distinct immutable replacing checkpoint.");
        }
    }

    private static void ValidateEvidenceShape(GovernedLoopHumanInputWaitingCheckpointEvidence? evidence, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (evidence is null)
        {
            Add(errors, "evidence_required", path, "Checkpoint evidence cannot be null.");
            return;
        }

        var errorCount = errors.Count;
        Schema(evidence.SchemaVersion, path + ".schemaVersion", errors);
        if (evidence.Sequence < 1 || evidence.Sequence > GovernedLoopHumanInputWaitingCheckpointContractLimits.MaxEvidenceEntries)
        {
            Add(errors, "invalid_evidence_sequence", path + ".sequence", "Evidence sequence must be positive and bounded.");
        }
        if (!Enum.IsDefined(evidence.Kind) || evidence.Kind == GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Unknown)
        {
            Add(errors, "unsupported_evidence_kind", path + ".kind", "Evidence kind must be a supported closed schema-1 value.");
        }
        Utc(evidence.OccurredAtUtc, path + ".occurredAtUtc", errors);
        if (evidence.Sequence == 1)
        {
            if (!string.IsNullOrEmpty(evidence.PreviousEvidenceHash)) Add(errors, "initial_evidence_predecessor", path + ".previousEvidenceHash", "Sequence-one evidence requires an empty predecessor hash.");
        }
        else
        {
            Hash(evidence.PreviousEvidenceHash, path + ".previousEvidenceHash", errors);
        }

        switch (evidence.Kind)
        {
            case GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered:
                if (!HumanInputResponseContractValidator.ValidateSelectionReference(evidence.AnswerSelection).IsValid || ContainsAuthorityTerm(evidence.AnswerSelection?.SelectionId)) Add(errors, "invalid_answer_selection", path + ".answerSelection", "Answer evidence requires one exact privacy-safe selection reference without authority semantics.");
                RequireAbsent(evidence.SupersedingCheckpointId, path + ".supersedingCheckpointId", errors);
                RequireAbsent(evidence.SupersedingCheckpointHash, path + ".supersedingCheckpointHash", errors);
                RequireAbsent(evidence.TerminalizationReceiptId, path + ".terminalizationReceiptId", errors);
                RequireAbsent(evidence.TerminalizationReceiptHash, path + ".terminalizationReceiptHash", errors);
                break;
            case GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Superseded:
                Identifier(evidence.SupersedingCheckpointId, path + ".supersedingCheckpointId", errors);
                Hash(evidence.SupersedingCheckpointHash, path + ".supersedingCheckpointHash", errors);
                RequireAbsent(evidence.AnswerSelection, path + ".answerSelection", errors);
                RequireAbsent(evidence.TerminalizationReceiptId, path + ".terminalizationReceiptId", errors);
                RequireAbsent(evidence.TerminalizationReceiptHash, path + ".terminalizationReceiptHash", errors);
                break;
            case GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized:
                Identifier(evidence.TerminalizationReceiptId, path + ".terminalizationReceiptId", errors);
                Hash(evidence.TerminalizationReceiptHash, path + ".terminalizationReceiptHash", errors);
                RequireAbsent(evidence.AnswerSelection, path + ".answerSelection", errors);
                RequireAbsent(evidence.SupersedingCheckpointId, path + ".supersedingCheckpointId", errors);
                RequireAbsent(evidence.SupersedingCheckpointHash, path + ".supersedingCheckpointHash", errors);
                break;
            default:
                RequireAbsent(evidence.AnswerSelection, path + ".answerSelection", errors);
                RequireAbsent(evidence.SupersedingCheckpointId, path + ".supersedingCheckpointId", errors);
                RequireAbsent(evidence.SupersedingCheckpointHash, path + ".supersedingCheckpointHash", errors);
                RequireAbsent(evidence.TerminalizationReceiptId, path + ".terminalizationReceiptId", errors);
                RequireAbsent(evidence.TerminalizationReceiptHash, path + ".terminalizationReceiptHash", errors);
                break;
        }

        Hash(evidence.EvidenceHash, path + ".evidenceHash", errors);
        if (errors.Count == errorCount && !GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(evidence))
        {
            Add(errors, "evidence_hash_mismatch", path + ".evidenceHash", "Evidence hash must exactly match its canonical schema-1 fields.");
        }
    }

    private static bool SelectionMatchesRequest(HumanInputResponseSelectionReference? selection, HumanInputRequest request)
        => selection is not null
            && selection.Request is not null
            && selection.Request.SchemaVersion == HumanInputRequestReference.CurrentSchemaVersion
            && string.Equals(selection.Request.RequestId, request.RequestId, StringComparison.Ordinal)
            && string.Equals(selection.Request.RequestVersionId, request.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(selection.Request.RequestHash, request.RequestHash, StringComparison.Ordinal);

    private static void Schema(int value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value != GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion) Add(errors, "unsupported_schema_version", path, "Only schema version 1 is supported.");
    }

    private static void Identifier(string? value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value) || ContainsAuthorityTerm(value)) Add(errors, "invalid_identifier", path, "Identifiers must be canonical Human Input data-only identifiers.");
    }

    private static void WorkspaceId(string? value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!ContextualRoleWorkspaceId.IsValid(value)) Add(errors, "invalid_workspace_id", path, "A canonical workspace-sha256 workspace scope is required.");
    }

    private static void Hash(string? value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (!GovernedLoopHumanInputWaitingCheckpointContractHash.IsSha256(value)) Add(errors, "invalid_hash", path, "A lowercase SHA-256 hash is required.");
    }

    private static void Utc(DateTimeOffset value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero) Add(errors, "timestamp_not_utc", path, "Timestamps must be non-default exact UTC instants.");
    }

    private static void RequireAbsent(object? value, string path, List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
    {
        if (value is not null) Add(errors, "unexpected_evidence_payload", path, "This evidence kind must not carry that payload.");
    }

    private static bool ContainsAuthorityTerm(string? value)
        => value is not null && _authorityTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void Add(List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors, string code, string path, string message)
        => errors.Add(new GovernedLoopHumanInputWaitingCheckpointValidationError(code, path, message));

    private static GovernedLoopHumanInputWaitingCheckpointValidationResult Result(List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors)
        => new(errors);
}
