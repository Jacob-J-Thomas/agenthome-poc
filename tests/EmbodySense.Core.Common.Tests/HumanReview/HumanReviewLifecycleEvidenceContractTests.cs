using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewLifecycleEvidenceContractTests
{
    [Fact]
    public void Run_state_defaults_lifecycle_history_to_its_admitted_lifecycle()
    {
        var request = HumanReviewTestData.Request();
        var lifecycle = HumanReviewTestData.Lifecycle(request);
        var evidence = HumanReviewTestData.Evidence(request);

        var state = new HumanReviewRunState(request, lifecycle, ImmutableArray.Create(evidence));

        Assert.False(state.LifecycleHistory.IsDefault);
        var retained = Assert.Single(state.LifecycleHistory);
        Assert.Equal(lifecycle.LifecycleHash, retained.LifecycleHash);
    }

    [Fact]
    public void Lifecycle_statuses_require_their_exact_decision_kind_and_stable_expiry_boundary()
    {
        var request = HumanReviewTestData.Request();
        var approve = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.Approve);
        var approved = HumanReviewTestData.Lifecycle(request, approve);
        var information = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.RequestInformation);
        var awaitingInformation = HumanReviewTestData.Lifecycle(request, information);
        var expiryAtBoundary = HumanReviewContractHash.ApplyLifecycle(HumanReviewTestData.Lifecycle(request, status: HumanReviewLifecycleStatus.Expired) with
        {
            UpdatedAtUtc = request.Timing.ExpiresAtUtc,
            Provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "coordinator-one", "expiry-correlation-one", request.Timing.ExpiresAtUtc, string.Empty)),
            LifecycleHash = string.Empty
        });

        Assert.True(HumanReviewContractValidator.ValidateLifecycle(request, approved).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateLifecycle(request, awaitingInformation).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateLifecycle(request, expiryAtBoundary).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateLifecycle(request, approved with { Status = HumanReviewLifecycleStatus.Rejected }).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateLifecycle(request, expiryAtBoundary with { UpdatedAtUtc = request.Timing.ExpiresAtUtc.AddTicks(-1) }).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateLifecycle(request, HumanReviewTestData.Lifecycle(request, status: HumanReviewLifecycleStatus.Pending) with { LastDecision = new HumanReviewDecisionReference(approve.DecisionId, approve.DecisionOperationId, approve.Kind, approve.DecisionHash) }).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateLifecycle(request, HumanReviewContractHash.ApplyLifecycle(approved with { Provenance = HumanReviewContractHash.ApplyProvenance(approved.Provenance with { ObservedAtUtc = approved.UpdatedAtUtc.AddTicks(1), ProvenanceHash = string.Empty }), LifecycleHash = string.Empty })).IsValid);
    }

    [Fact]
    public void Evidence_requires_exact_request_closed_event_decision_pairing_and_safe_provenance()
    {
        var request = HumanReviewTestData.Request();
        var approve = HumanReviewTestData.Decision(request);
        var admitted = HumanReviewTestData.Evidence(request);
        var accepted = HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.DecisionAccepted, approve);
        var information = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.RequestInformation);
        var informationEvidence = HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.InformationRequested, information);
        var continuation = HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.ContinuationReserved, approve);
        var variants = new HumanReviewEvidence[]
        {
            admitted with { SchemaVersion = 2 },
            admitted with { EvidenceId = "Invalid" },
            admitted with { Request = new HumanReviewRequestReference("review-request-other", request.RequestHash) },
            HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.DecisionAccepted),
            HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.InformationRequested, approve),
            HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.RequestAdmitted, approve),
            HumanReviewContractHash.ApplyEvidence(admitted with { DecisionOperation = accepted.DecisionOperation, EvidenceHash = string.Empty }),
            admitted with { RecordedAtUtc = request.Timing.CreatedAtUtc.AddTicks(-1) },
            HumanReviewContractHash.ApplyEvidence(admitted with { Previews = [HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "credential=private", string.Empty))], EvidenceHash = string.Empty }),
            HumanReviewContractHash.ApplyEvidence(admitted with { Previews = default, EvidenceHash = string.Empty }),
            HumanReviewContractHash.ApplyEvidence(admitted with { Provenance = HumanReviewContractHash.ApplyProvenance(admitted.Provenance with { Kind = HumanReviewProvenanceKind.AuthenticatedReviewer, ProvenanceHash = string.Empty }), EvidenceHash = string.Empty }),
            HumanReviewContractHash.ApplyEvidence(admitted with { Provenance = HumanReviewContractHash.ApplyProvenance(admitted.Provenance with { ObservedAtUtc = admitted.RecordedAtUtc.AddTicks(1), ProvenanceHash = string.Empty }), EvidenceHash = string.Empty })
        };

        var admittedValidation = HumanReviewContractValidator.ValidateEvidence(request, admitted);
        Assert.True(admittedValidation.IsValid, string.Join("; ", admittedValidation.Errors.Select(error => $"{error.Code}:{error.Path}")));
        Assert.True(HumanReviewContractValidator.ValidateEvidence(request, accepted).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateEvidence(request, informationEvidence).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateEvidence(request, continuation).IsValid);
        Assert.All(variants, variant => Assert.False(HumanReviewContractValidator.ValidateEvidence(request, variant).IsValid));
        Assert.False(HumanReviewContractValidator.ValidateEvidence(request, null).IsValid);
    }

    [Fact]
    public void Every_required_append_only_event_has_one_exact_decision_reference_rule()
    {
        var request = HumanReviewTestData.Request();
        var approve = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.Approve);
        var information = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.RequestInformation);
        var noDecisionKinds = new[]
        {
            HumanReviewEvidenceKind.RequestAdmitted,
            HumanReviewEvidenceKind.RequestPublished,
            HumanReviewEvidenceKind.ReminderRecorded,
            HumanReviewEvidenceKind.EscalationRecorded,
            HumanReviewEvidenceKind.RequestConflict,
            HumanReviewEvidenceKind.RequestExpired,
            HumanReviewEvidenceKind.RequestSuperseded,
            HumanReviewEvidenceKind.DecisionConflict,
            HumanReviewEvidenceKind.DecisionDenied,
            HumanReviewEvidenceKind.DecisionExpired,
        };
        var decisionKinds = new[]
        {
            (HumanReviewEvidenceKind.DecisionAttempted, information),
            (HumanReviewEvidenceKind.DecisionAccepted, approve),
            (HumanReviewEvidenceKind.InformationRequested, information),
            (HumanReviewEvidenceKind.ContinuationReserved, approve),
            (HumanReviewEvidenceKind.ContinuationCompleted, approve),
            (HumanReviewEvidenceKind.PreDispatchBlocked, approve)
        };

        Assert.All(noDecisionKinds, kind => Assert.True(HumanReviewContractValidator.ValidateEvidence(request, HumanReviewTestData.Evidence(request, kind)).IsValid));
        Assert.All(decisionKinds, pair => Assert.True(HumanReviewContractValidator.ValidateEvidence(request, HumanReviewTestData.Evidence(request, pair.Item1, pair.Item2)).IsValid));
        Assert.False(HumanReviewContractValidator.ValidateEvidence(request, HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.DecisionAttempted)).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateEvidence(request, HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.RequestPublished, approve)).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateEvidence(request, HumanReviewTestData.Evidence(request, HumanReviewEvidenceKind.PreDispatchBlocked)).IsValid);
    }

    [Fact]
    public void Lifecycle_and_evidence_hashes_are_canonical_mutation_sensitive_and_defensively_snapshotted()
    {
        var request = HumanReviewTestData.Request();
        var lifecycle = HumanReviewTestData.Lifecycle(request);
        var evidence = HumanReviewContractHash.ApplyEvidence(HumanReviewTestData.Evidence(request) with
        {
            Previews = [HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "A retained redacted evidence summary.", string.Empty))],
            EvidenceHash = string.Empty
        });
        var equivalentLifecycle = HumanReviewContractHash.ApplyLifecycle(lifecycle with { Request = lifecycle.Request with { }, Provenance = lifecycle.Provenance with { }, LifecycleHash = string.Empty });
        var equivalentEvidence = HumanReviewContractHash.ApplyEvidence(evidence with { Request = evidence.Request with { }, Previews = evidence.Previews.ToImmutableArray(), Provenance = evidence.Provenance with { }, EvidenceHash = string.Empty });

        Assert.Equal(lifecycle.LifecycleHash, equivalentLifecycle.LifecycleHash);
        Assert.Equal(evidence.EvidenceHash, equivalentEvidence.EvidenceHash);
        Assert.False(HumanReviewContractHash.MatchesLifecycle(lifecycle with { LifecycleHash = HumanReviewTestData.Hash('a') }));
        Assert.False(HumanReviewContractHash.MatchesEvidence(evidence with { EvidenceHash = HumanReviewTestData.Hash('b') }));
        Assert.True(HumanReviewContractSnapshot.TryCaptureLifecycle(request, lifecycle, out var lifecycleSnapshot, out var lifecycleValidation));
        Assert.True(HumanReviewContractSnapshot.TryCaptureEvidence(request, evidence, out var evidenceSnapshot, out var evidenceValidation));
        Assert.True(lifecycleValidation.IsValid);
        Assert.True(evidenceValidation.IsValid);
        Assert.NotSame(lifecycle.Request, lifecycleSnapshot!.Request);
        Assert.Equal(evidence.Previews.ToArray(), evidenceSnapshot!.Previews);
        Assert.True(HumanReviewContractValidator.ValidateLifecycle(request, lifecycleSnapshot).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateEvidence(request, evidenceSnapshot).IsValid);
    }

    [Fact]
    public void Lifecycle_capture_fails_closed_for_null_nested_contracts_without_throwing()
    {
        var request = HumanReviewTestData.Request();
        var malformed = HumanReviewTestData.Lifecycle(request) with { Request = null!, Provenance = null! };

        var captured = HumanReviewContractSnapshot.TryCaptureLifecycle(request, malformed, out var snapshot, out var validation);

        Assert.False(captured);
        Assert.Null(snapshot);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code is "request_reference_required" or "provenance_required");
    }

    [Fact]
    public void Retained_contract_to_string_values_redact_preview_detail_and_private_attribution()
    {
        var request = HumanReviewTestData.Request();
        var decision = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.RequestInformation, "A private reviewer clarification.");
        var lifecycle = HumanReviewTestData.Lifecycle(request, decision);
        var evidence = HumanReviewContractHash.ApplyEvidence(HumanReviewTestData.Evidence(request) with
        {
            Previews = ImmutableArray.Create(HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "A private evidence detail.", string.Empty))),
            EvidenceHash = string.Empty
        });
        var malformedRequest = request with { RequestedDecisions = default };
        var rendered = new[] { request.ToString(), decision.ToString(), lifecycle.ToString(), evidence.ToString(), request.Previews[0].ToString(), request.Provenance.ToString(), request.EligibleReviewers[0].ToString() };

        Assert.Contains("RequestedDecisionCount = 0", malformedRequest.ToString());
        Assert.All(rendered, text => Assert.Contains("[REDACTED]", text));
        Assert.All(rendered, text => Assert.DoesNotContain("A redacted action summary.", text));
        Assert.All(rendered, text => Assert.DoesNotContain("A private reviewer clarification.", text));
        Assert.All(rendered, text => Assert.DoesNotContain("A private evidence detail.", text));
        Assert.All(rendered, text => Assert.DoesNotContain("reviewer-one", text));
        Assert.All(rendered, text => Assert.DoesNotContain("scope-alpha", text));
        Assert.All(rendered, text => Assert.DoesNotContain("server-one", text));
    }
}
