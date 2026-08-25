using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

internal static class HumanReviewTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    internal static HumanReviewRequest Request(HumanReviewPurpose purpose = HumanReviewPurpose.Continuation, HumanReviewDecisionKind[]? requestedDecisions = null, HumanReviewReviewerScope[]? reviewers = null, HumanReviewRedactedPreview[]? previews = null, HumanReviewTiming? timing = null)
    {
        var effect = purpose == HumanReviewPurpose.PreDispatchEffect
            ? HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding("effect-attempt-one", "operation-one", 1, Hash('a'), Hash('b'), HumanReviewEffectDispatchCertainty.NotDispatched, string.Empty))
            : null;
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            WorkspaceId,
            "run-one",
            "graph-one",
            "revision-one",
            Hash('c'),
            "node-one",
            0,
            null,
            1,
            "frontier-one",
            1,
            Hash('d'),
            Hash('e'),
            Hash('f'),
            Hash('1'),
            Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            effect,
            string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(
            purpose == HumanReviewPurpose.PreDispatchEffect ? HumanReviewApprovalScopeKind.PreDispatchEffect : HumanReviewApprovalScopeKind.Continuation,
            binding.BindingHash,
            effect?.EffectAttemptId,
            string.Empty));
        var request = new HumanReviewRequest(
            1,
            "review-request-one",
            "review-request-operation-one",
            binding,
            purpose,
            (requestedDecisions ?? [HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation]).ToImmutableArray(),
            (reviewers ?? [new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"))]).ToImmutableArray(),
            scope,
            (previews ?? [
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "A redacted action summary.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "A redacted expected result.", string.Empty)),
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "A redacted evidence summary.", string.Empty))]).ToImmutableArray(),
            timing ?? new HumanReviewTiming(CreatedAtUtc, CreatedAtUtc.AddMinutes(10), CreatedAtUtc.AddHours(1)),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "server-one", "request-correlation-one", CreatedAtUtc, string.Empty)),
            string.Empty);
        return HumanReviewContractHash.ApplyRequest(request);
    }

    internal static HumanReviewDecision Decision(HumanReviewRequest request, HumanReviewDecisionKind kind = HumanReviewDecisionKind.Approve, string? detail = null)
    {
        var decidedAtUtc = CreatedAtUtc.AddMinutes(1);
        var decision = new HumanReviewDecision(
            1,
            "review-decision-one",
            "review-decision-operation-one",
            new HumanReviewRequestReference(request.RequestId, request.RequestHash),
            kind,
            "reviewer-one",
            "reviewer-role-one",
            ImmutableArray.Create("scope-alpha", "scope-beta"),
            decidedAtUtc,
            detail ?? (kind == HumanReviewDecisionKind.RequestInformation ? "Please provide a redacted clarification." : null),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.AuthenticatedReviewer, "reviewer-one", "decision-correlation-one", decidedAtUtc, string.Empty)),
            string.Empty);
        return HumanReviewContractHash.ApplyDecision(decision);
    }

    internal static HumanReviewLifecycle Lifecycle(HumanReviewRequest request, HumanReviewDecision? decision = null, HumanReviewLifecycleStatus? status = null)
    {
        var selectedStatus = status ?? (decision?.Kind switch
        {
            HumanReviewDecisionKind.Approve => HumanReviewLifecycleStatus.Approved,
            HumanReviewDecisionKind.Reject => HumanReviewLifecycleStatus.Rejected,
            HumanReviewDecisionKind.Cancel => HumanReviewLifecycleStatus.Cancelled,
            HumanReviewDecisionKind.RequestInformation => HumanReviewLifecycleStatus.AwaitingInformation,
            _ => HumanReviewLifecycleStatus.Pending
        });
        var lifecycle = new HumanReviewLifecycle(
            1,
            new HumanReviewRequestReference(request.RequestId, request.RequestHash),
            selectedStatus,
            1,
            CreatedAtUtc.AddMinutes(2),
            decision is null ? null : new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "server-one", "lifecycle-correlation-one", CreatedAtUtc.AddMinutes(2), string.Empty)),
            null,
            string.Empty);
        return HumanReviewContractHash.ApplyLifecycle(lifecycle);
    }

    internal static HumanReviewEvidence Evidence(HumanReviewRequest request, HumanReviewEvidenceKind kind = HumanReviewEvidenceKind.RequestAdmitted, HumanReviewDecision? decision = null)
    {
        var recordedAtUtc = CreatedAtUtc.AddMinutes(3);
        var evidence = new HumanReviewEvidence(
            1,
            "review-evidence-one",
            new HumanReviewRequestReference(request.RequestId, request.RequestHash),
            kind,
            decision is null ? null : new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash),
            recordedAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "coordinator-one", "evidence-correlation-one", recordedAtUtc, string.Empty)),
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            null,
            string.Empty);
        if (kind is HumanReviewEvidenceKind.DecisionAccepted or HumanReviewEvidenceKind.InformationRequested or HumanReviewEvidenceKind.DecisionConflict or HumanReviewEvidenceKind.DecisionDenied or HumanReviewEvidenceKind.DecisionExpired)
        {
            evidence = evidence with
            {
                DecisionOperation = new HumanReviewDecisionOperationReference(decision?.DecisionOperationId ?? "review-decision-operation-one", Hash('f'), kind switch
                {
                    HumanReviewEvidenceKind.DecisionAccepted => HumanReviewDecisionOperationDisposition.Accepted,
                    HumanReviewEvidenceKind.InformationRequested => HumanReviewDecisionOperationDisposition.InformationRequested,
                    HumanReviewEvidenceKind.DecisionDenied => HumanReviewDecisionOperationDisposition.Denied,
                    HumanReviewEvidenceKind.DecisionExpired => HumanReviewDecisionOperationDisposition.Expired,
                    _ => HumanReviewDecisionOperationDisposition.Conflict
                }, Hash('e'))
            };
        }
        else if (kind == HumanReviewEvidenceKind.ContinuationReserved)
        {
            evidence = evidence with { ContinuationReservation = new HumanReviewContinuationReservationReference("review-reservation-one", Hash('d')) };
        }
        return HumanReviewContractHash.ApplyEvidence(evidence);
    }

    internal static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    internal static string WorkspaceId => "workspace-sha256:" + Hash('a');
}
