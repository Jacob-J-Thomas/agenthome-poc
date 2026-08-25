using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewDecisionContractTests
{
    [Theory]
    [InlineData(HumanReviewDecisionKind.Approve)]
    [InlineData(HumanReviewDecisionKind.Reject)]
    [InlineData(HumanReviewDecisionKind.Cancel)]
    [InlineData(HumanReviewDecisionKind.RequestInformation)]
    public void Every_closed_requested_decision_validates_against_the_exact_request(HumanReviewDecisionKind kind)
    {
        var request = HumanReviewTestData.Request();
        var decision = HumanReviewTestData.Decision(request, kind);

        var validation = HumanReviewContractValidator.ValidateDecision(request, decision);

        Assert.True(validation.IsValid);
        Assert.True(HumanReviewContractHash.MatchesDecision(decision));
    }

    [Fact]
    public void Decision_validation_rejects_identity_request_reviewer_scope_time_detail_and_provenance_drift()
    {
        var request = HumanReviewTestData.Request(requestedDecisions: [HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation]);
        var decision = HumanReviewTestData.Decision(request);
        var variants = new HumanReviewDecision[]
        {
            decision with { SchemaVersion = 2 },
            decision with { DecisionId = "Invalid" },
            decision with { DecisionId = decision.DecisionOperationId },
            decision with { Request = new HumanReviewRequestReference("review-request-other", request.RequestHash) },
            decision with { Kind = (HumanReviewDecisionKind)99 },
            decision with { ReviewerRoleId = "reviewer-role-other" },
            decision with { ReviewerScopeIds = ImmutableArray.Create("scope-beta", "scope-alpha") },
            decision with { ReviewerScopeIds = default },
            decision with { DecidedAtUtc = request.Timing.ExpiresAtUtc.AddTicks(1) },
            decision with { Detail = new string('x', HumanReviewContractLimits.MaxDecisionDetailCharacters + 1) },
            HumanReviewContractHash.ApplyDecision(decision with { Kind = HumanReviewDecisionKind.RequestInformation, Detail = null, DecisionHash = string.Empty }),
            HumanReviewContractHash.ApplyDecision(decision with { Detail = "token=private", DecisionHash = string.Empty }),
            HumanReviewContractHash.ApplyDecision(decision with { Provenance = HumanReviewContractHash.ApplyProvenance(decision.Provenance with { SourceId = "reviewer-other", ProvenanceHash = string.Empty }), DecisionHash = string.Empty })
        };

        Assert.All(variants, variant => Assert.False(HumanReviewContractValidator.ValidateDecision(request, variant).IsValid));
        Assert.False(HumanReviewContractValidator.ValidateDecision(request, null).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateDecision(null, decision).IsValid);
    }

    [Fact]
    public void Offered_decision_set_and_exact_purpose_scope_fail_closed_when_the_request_does_not_allow_the_decision()
    {
        var onlyReject = HumanReviewTestData.Request(requestedDecisions: [HumanReviewDecisionKind.Reject]);
        var approval = HumanReviewTestData.Decision(onlyReject, HumanReviewDecisionKind.Approve);
        var effectRequest = HumanReviewTestData.Request(HumanReviewPurpose.PreDispatchEffect);
        var invalidEffect = HumanReviewContractHash.ApplyRequest(effectRequest with
        {
            Binding = HumanReviewContractHash.ApplyBinding(effectRequest.Binding with
            {
                EffectAttempt = HumanReviewContractHash.ApplyEffectAttempt(effectRequest.Binding.EffectAttempt! with { DispatchCertainty = HumanReviewEffectDispatchCertainty.Dispatched, EffectAttemptHash = string.Empty }),
                BindingHash = string.Empty
            }),
            RequestHash = string.Empty
        });

        Assert.True(HumanReviewContractValidator.ValidateRequest(onlyReject).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateDecision(onlyReject, approval).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateRequest(invalidEffect).IsValid);
    }

    [Fact]
    public void Decision_snapshots_defensively_copy_scopes_and_preserve_hash_stability()
    {
        var request = HumanReviewTestData.Request();
        var decision = HumanReviewTestData.Decision(request);
        var equivalent = HumanReviewContractHash.ApplyDecision(decision with
        {
            ReviewerScopeIds = decision.ReviewerScopeIds.ToImmutableArray(),
            Request = decision.Request with { },
            Provenance = decision.Provenance with { },
            DecisionHash = string.Empty
        });

        Assert.True(HumanReviewContractSnapshot.TryCaptureDecision(request, decision, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);

        var mutableScopes = decision.ReviewerScopeIds.ToArray();
        mutableScopes[0] = "scope-mutated";

        Assert.Equal(decision.DecisionHash, equivalent.DecisionHash);
        Assert.Equal("scope-alpha", decision.ReviewerScopeIds[0]);
        Assert.Equal("scope-alpha", snapshot.ReviewerScopeIds[0]);
        Assert.True(HumanReviewContractValidator.ValidateDecision(request, snapshot).IsValid);
        Assert.Equal(decision.DecisionHash, HumanReviewContractHash.ComputeDecision(decision));
    }

    [Fact]
    public void Decision_snapshot_allocates_new_backing_storage_for_hostile_immutable_scope_array()
    {
        var request = HumanReviewTestData.Request();
        var valid = HumanReviewTestData.Decision(request);
        var mutableScopes = valid.ReviewerScopeIds.ToArray();
        var decision = HumanReviewContractHash.ApplyDecision(valid with { ReviewerScopeIds = ImmutableCollectionsMarshal.AsImmutableArray(mutableScopes), DecisionHash = string.Empty });

        Assert.True(HumanReviewContractSnapshot.TryCaptureDecision(request, decision, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);

        mutableScopes[0] = "scope-mutated";

        Assert.Equal("scope-alpha", snapshot.ReviewerScopeIds[0]);
        Assert.True(HumanReviewContractValidator.ValidateDecision(request, snapshot).IsValid);
    }
}
