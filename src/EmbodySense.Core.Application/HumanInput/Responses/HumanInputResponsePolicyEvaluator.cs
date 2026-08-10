using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

internal static class HumanInputResponsePolicyEvaluator
{
    internal static HumanInputResponseLifecycleMutationPlan Evaluate(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId actorId,
        string actorRoleId,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<HumanInputResponseArtifact> retainedResponses,
        IReadOnlyList<HumanInputResponseArtifact> activeResponses)
    {
        return command.Kind switch
        {
            HumanInputResponseOperationKind.Submit => Submit(request, head, command, actorId, actorRoleId, recordedAtUtc, retainedResponses, activeResponses),
            HumanInputResponseOperationKind.Withdraw => Withdraw(request, head, command, actorId, retainedResponses, activeResponses),
            HumanInputResponseOperationKind.Select => Select(request, head, command, actorId, actorRoleId, recordedAtUtc, retainedResponses, activeResponses),
            _ => Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Ambiguous, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OptimisticStateConflict, canPersist: false),
        };
    }

    internal static HumanInputResponseLifecycleMutationPlan Failure(
        HumanInputRequestLifecycleHead? observed,
        ImmutableArray<HumanInputResponseReference> targets,
        HumanInputResponseLifecycleMutationStatus status,
        HumanInputResponseOperationOutcome outcome,
        HumanInputResponseOperationFailureCode failureCode,
        bool canPersist = true)
        => new(status, outcome, failureCode, observed, observed, null, targets, null, canPersist);

    private static HumanInputResponseLifecycleMutationPlan Submit(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId actorId,
        string actorRoleId,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<HumanInputResponseArtifact> retainedResponses,
        IReadOnlyList<HumanInputResponseArtifact> activeResponses)
    {
        if (retainedResponses.Any(response => string.Equals(response.ResponseId, command.ResponseId, StringComparison.Ordinal))
            || activeResponses.Any(response => response.ActorId.Equals(actorId)))
        {
            return Failure(head, [], HumanInputResponseLifecycleMutationStatus.Conflict, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.DuplicateResponse);
        }
        if (retainedResponses.Count >= HumanInputResponseContractLimits.MaxResponsesPerRequest)
        {
            return Failure(head, [], HumanInputResponseLifecycleMutationStatus.LimitExceeded, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.ResponseLimitExceeded);
        }

        var requestReference = Reference(request);
        var valueHash = HumanInputResponseValueHash.Compute(command.Value!);
        var artifact = HumanInputResponseArtifactHash.Apply(
            new HumanInputResponseArtifact(
                HumanInputResponseContractLimits.CurrentSchemaVersion,
                command.ResponseId!,
                requestReference,
                request.Binding,
                actorId,
                actorRoleId,
                recordedAtUtc,
                request.PrivacyClass,
                command.Value!,
                command.Explanation,
                valueHash,
                string.Empty));
        if (!HumanInputResponseReference.TryCreate(request, artifact, out var responseReference, out _)
            || responseReference is null)
        {
            return Failure(head, [], HumanInputResponseLifecycleMutationStatus.Ambiguous, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OptimisticStateConflict, canPersist: false);
        }

        var active = activeResponses.Append(artifact).ToArray();
        if (!HumanInputResponseAutomaticPolicyDecision.TryEvaluate(request, command.OperationId, recordedAtUtc, active, out var selection))
        {
            return Failure(head, [], HumanInputResponseLifecycleMutationStatus.Ambiguous, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.OptimisticStateConflict, canPersist: false);
        }
        if (selection is null)
        {
            return new HumanInputResponseLifecycleMutationPlan(
                HumanInputResponseLifecycleMutationStatus.Committed,
                HumanInputResponseOperationOutcome.Committed,
                HumanInputResponseOperationFailureCode.None,
                head,
                head,
                artifact,
                [],
                null,
                true);
        }
        return Selected(head, command.OperationId, recordedAtUtc, artifact, [], selection);
    }

    private static HumanInputResponseLifecycleMutationPlan Withdraw(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId actorId,
        IReadOnlyList<HumanInputResponseArtifact> retainedResponses,
        IReadOnlyList<HumanInputResponseArtifact> activeResponses)
    {
        var target = command.TargetResponses[0];
        var retained = retainedResponses.SingleOrDefault(response => target.Matches(request, response));
        if (retained is null)
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.NotFound, HumanInputResponseOperationOutcome.NotFound, HumanInputResponseOperationFailureCode.ResponseNotFound);
        }
        var active = activeResponses.SingleOrDefault(response => target.Matches(request, response));
        if (active is null)
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Conflict, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.ResponseAlreadyWithdrawn);
        }
        if (!active.ActorId.Equals(actorId))
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Ineligible, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.IneligibleRespondent);
        }
        return new HumanInputResponseLifecycleMutationPlan(
            HumanInputResponseLifecycleMutationStatus.Committed,
            HumanInputResponseOperationOutcome.Committed,
            HumanInputResponseOperationFailureCode.None,
            head,
            head,
            null,
            command.TargetResponses,
            null,
            true);
    }

    private static HumanInputResponseLifecycleMutationPlan Select(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId actorId,
        string actorRoleId,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<HumanInputResponseArtifact> retainedResponses,
        IReadOnlyList<HumanInputResponseArtifact> activeResponses)
    {
        if (request.ResponsePolicy.Kind != HumanInputResponsePolicyKind.ManualSelection
            || request.ResponsePolicy.OrderedRoleIds is not { } selectorRoles
            || !selectorRoles.Contains(actorRoleId, StringComparer.Ordinal))
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Ineligible, HumanInputResponseOperationOutcome.Rejected, HumanInputResponseOperationFailureCode.IneligibleSelector);
        }
        var target = command.TargetResponses[0];
        if (!retainedResponses.Any(response => target.Matches(request, response)))
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.NotFound, HumanInputResponseOperationOutcome.NotFound, HumanInputResponseOperationFailureCode.ResponseNotFound);
        }
        if (!activeResponses.Any(response => target.Matches(request, response))
            || recordedAtUtc > request.Timing.ExpiresAtUtc)
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Conflict, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.SelectionConflict);
        }

        var selection = HumanInputResponseSelectionHash.Apply(
            new HumanInputResponseSelection(
                HumanInputResponseContractLimits.CurrentSchemaVersion,
                command.OperationId,
                Reference(request),
                request.ResponsePolicy.Kind,
                command.TargetResponses,
                actorId,
                actorRoleId,
                recordedAtUtc,
                string.Empty));
        if (!HumanInputResponseContractValidator.ValidateSelection(request, selection, activeResponses).IsValid)
        {
            return Failure(head, command.TargetResponses, HumanInputResponseLifecycleMutationStatus.Conflict, HumanInputResponseOperationOutcome.Conflict, HumanInputResponseOperationFailureCode.SelectionConflict);
        }
        return Selected(head, command.OperationId, recordedAtUtc, null, command.TargetResponses, selection);
    }

    private static HumanInputResponseLifecycleMutationPlan Selected(
        HumanInputRequestLifecycleHead head,
        string operationId,
        DateTimeOffset recordedAtUtc,
        HumanInputResponseArtifact? response,
        ImmutableArray<HumanInputResponseReference> targets,
        HumanInputResponseSelection selection)
    {
        if (head.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            return Failure(head, targets, HumanInputResponseLifecycleMutationStatus.LimitExceeded, HumanInputResponseOperationOutcome.LimitExceeded, HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded);
        }
        var selectionReference = HumanInputResponseSelectionReference.Create(selection);
        var result = head with
        {
            LifecycleVersion = head.LifecycleVersion + 1,
            Status = HumanInputRequestLifecycleStatus.Answered,
            LastOperationId = operationId,
            UpdatedAtUtc = recordedAtUtc,
            AnswerSelection = selectionReference,
        };
        return new HumanInputResponseLifecycleMutationPlan(
            HumanInputResponseLifecycleMutationStatus.Committed,
            HumanInputResponseOperationOutcome.Committed,
            HumanInputResponseOperationFailureCode.None,
            head,
            result,
            response,
            targets,
            selection,
            true);
    }

    private static HumanInputRequestReference Reference(HumanInputRequest request)
        => new(HumanInputResponseContractLimits.CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash);
}
