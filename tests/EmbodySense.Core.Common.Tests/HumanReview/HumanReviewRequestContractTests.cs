using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewRequestContractTests
{
    [Fact]
    public void Valid_continuation_request_carries_exact_immutable_consent_scope_without_authority_behavior()
    {
        var request = HumanReviewTestData.Request();

        var validation = HumanReviewContractValidator.ValidateRequest(request);

        Assert.True(validation.IsValid);
        Assert.Equal(HumanReviewPurpose.Continuation, request.Purpose);
        Assert.Equal(HumanReviewApprovalScopeKind.Continuation, request.ApprovalScope.Kind);
        Assert.Null(request.Binding.EffectAttempt);
        Assert.Equal(request.Binding.BindingHash, request.ApprovalScope.BindingHash);
        Assert.Equal([HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation], request.RequestedDecisions.ToArray());
        Assert.True(HumanReviewContractHash.MatchesRequest(request));
    }

    [Fact]
    public void Valid_pre_dispatch_effect_request_binds_only_one_conclusively_not_dispatched_attempt()
    {
        var request = HumanReviewTestData.Request(HumanReviewPurpose.PreDispatchEffect);

        var validation = HumanReviewContractValidator.ValidateRequest(request);

        Assert.True(validation.IsValid);
        Assert.NotNull(request.Binding.EffectAttempt);
        Assert.Equal(HumanReviewEffectDispatchCertainty.NotDispatched, request.Binding.EffectAttempt!.DispatchCertainty);
        Assert.Equal(HumanReviewApprovalScopeKind.PreDispatchEffect, request.ApprovalScope.Kind);
        Assert.Equal(request.Binding.EffectAttempt.EffectAttemptId, request.ApprovalScope.EffectAttemptId);
    }

    [Fact]
    public void Request_validation_rejects_unknown_malformed_oversized_noncanonical_time_and_secret_bearing_shapes()
    {
        var valid = HumanReviewTestData.Request();
        var reviewersAtLimit = Enumerable.Range(0, HumanReviewContractLimits.MaxEligibleReviewers)
            .Select(index => new HumanReviewReviewerScope($"reviewer-role-{index:D2}", [$"scope-{index:D2}"]))
            .ToArray();
        var validAtLimit = HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = reviewersAtLimit.ToImmutableArray(), RequestHash = string.Empty });
        var scopesAtLimit = Enumerable.Range(0, HumanReviewContractLimits.MaxScopesPerReviewer).Select(index => $"scope-{index:D2}").ToArray();
        var validScopesAtLimit = HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", scopesAtLimit.ToImmutableArray())), RequestHash = string.Empty });
        var validWindowAtLimit = HumanReviewContractHash.ApplyRequest(valid with { Timing = new HumanReviewTiming(valid.Timing.CreatedAtUtc, valid.Timing.CreatedAtUtc, valid.Timing.CreatedAtUtc.Add(HumanReviewContractLimits.MaxReviewWindow)), RequestHash = string.Empty });
        var oversizedReviewers = Enumerable.Range(0, HumanReviewContractLimits.MaxEligibleReviewers + 1)
            .Select(index => new HumanReviewReviewerScope($"reviewer-role-{index:D2}", [$"scope-{index:D2}"]))
            .ToArray();
        var variants = new HumanReviewRequest[]
        {
            valid with { SchemaVersion = 2 },
            valid with { RequestId = "Invalid" },
            valid with { RequestId = new string('a', HumanReviewContractLimits.MaxIdentifierCharacters + 1) },
            valid with { Purpose = (HumanReviewPurpose)99 },
            HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = oversizedReviewers.ToImmutableArray(), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", Enumerable.Range(0, HumanReviewContractLimits.MaxScopesPerReviewer + 1).Select(index => $"scope-{index:D2}").ToImmutableArray())), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-beta", "scope-alpha"))), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { RequestedDecisions = ImmutableArray.Create(HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Approve), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { RequestedDecisions = ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation, HumanReviewDecisionKind.RequestInformation), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Previews = ImmutableArray.Create(valid.Previews[2], valid.Previews[1], valid.Previews[0]), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Previews = valid.Previews.Concat([HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Second action", "A second redacted action summary.", string.Empty))]).ToImmutableArray(), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Previews = ImmutableArray.Create(
                HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "password=private", string.Empty)),
                valid.Previews[1],
                valid.Previews[2]), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { RequestedDecisions = default, RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = default, RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Previews = default, RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { EligibleReviewers = ImmutableArray.Create(valid.EligibleReviewers[0] with { ScopeIds = default }), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Timing = new HumanReviewTiming(valid.Timing.DueAtUtc, valid.Timing.CreatedAtUtc, valid.Timing.ExpiresAtUtc), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Timing = new HumanReviewTiming(valid.Timing.CreatedAtUtc, valid.Timing.CreatedAtUtc, valid.Timing.CreatedAtUtc.Add(HumanReviewContractLimits.MaxReviewWindow).AddTicks(1)), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Provenance = HumanReviewContractHash.ApplyProvenance(valid.Provenance with { ObservedAtUtc = valid.Timing.CreatedAtUtc.AddTicks(1), ProvenanceHash = string.Empty }), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Binding = HumanReviewContractHash.ApplyBinding(valid.Binding with { ActivationOrdinal = 0, VisitOrdinal = 1, BindingHash = string.Empty }), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Binding = HumanReviewContractHash.ApplyBinding(valid.Binding with { Attempt = HumanReviewContractLimits.MaxNodeAttempt + 1, BindingHash = string.Empty }), RequestHash = string.Empty }),
            HumanReviewContractHash.ApplyRequest(valid with { Binding = HumanReviewContractHash.ApplyBinding(valid.Binding with { EffectAttempt = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding("effect-attempt-one", "operation-one", 1, HumanReviewTestData.Hash('a'), HumanReviewTestData.Hash('b'), HumanReviewEffectDispatchCertainty.Ambiguous, string.Empty)), BindingHash = string.Empty }), RequestHash = string.Empty })
        };

        Assert.True(HumanReviewContractValidator.ValidateRequest(validAtLimit).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateRequest(validScopesAtLimit).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateRequest(validWindowAtLimit).IsValid);
        Assert.All(variants, variant => Assert.False(HumanReviewContractValidator.ValidateRequest(variant).IsValid));
        Assert.False(HumanReviewContractValidator.ValidateRequest(null).IsValid);
    }

    [Fact]
    public void Request_hashes_are_stable_for_equivalent_canonical_values_and_sensitive_to_every_bound_value()
    {
        var request = HumanReviewTestData.Request();
        var equivalent = HumanReviewContractHash.ApplyRequest(request with
        {
            Binding = request.Binding with { },
            RequestedDecisions = request.RequestedDecisions.ToImmutableArray(),
            EligibleReviewers = request.EligibleReviewers.Select(reviewer => reviewer with { ScopeIds = reviewer.ScopeIds.ToImmutableArray() }).ToImmutableArray(),
            ApprovalScope = request.ApprovalScope with { },
            Previews = request.Previews.Select(preview => preview with { }).ToImmutableArray(),
            Timing = request.Timing with { },
            Provenance = request.Provenance with { },
            RequestHash = string.Empty
        });
        var changed = HumanReviewContractHash.ApplyRequest(request with
        {
            Previews = ImmutableArray.Create(
                HumanReviewContractHash.ApplyPreview(request.Previews[0] with { Detail = "A different redacted action summary.", DetailHash = string.Empty }),
                request.Previews[1],
                request.Previews[2]),
            RequestHash = string.Empty
        });

        Assert.Equal(request.RequestHash, equivalent.RequestHash);
        Assert.NotEqual(request.RequestHash, changed.RequestHash);
        Assert.True(HumanReviewContractHash.MatchesRequest(equivalent));
        Assert.False(HumanReviewContractHash.MatchesRequest(request with { RequestHash = HumanReviewTestData.Hash('d') }));
        Assert.False(HumanReviewContractValidator.ValidateRequest(request with { RequestHash = HumanReviewTestData.Hash('d') }).IsValid);
    }

    [Fact]
    public void Request_snapshot_defensively_copies_nested_arrays_and_keeps_the_captured_contract_valid()
    {
        var request = HumanReviewTestData.Request();

        Assert.True(HumanReviewContractSnapshot.TryCaptureRequest(request, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);

        var mutableDecisions = request.RequestedDecisions.ToArray();
        var mutableScopes = request.EligibleReviewers[0].ScopeIds.ToArray();
        var mutablePreviews = request.Previews.ToArray();
        mutableDecisions[0] = HumanReviewDecisionKind.Cancel;
        mutableScopes[0] = "scope-mutated";
        mutablePreviews[0] = HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "A mutated redacted action summary.", string.Empty));

        Assert.Equal(HumanReviewDecisionKind.Approve, request.RequestedDecisions[0]);
        Assert.Equal("scope-alpha", request.EligibleReviewers[0].ScopeIds[0]);
        Assert.Equal("A redacted action summary.", request.Previews[0].Detail);
        Assert.Equal(HumanReviewDecisionKind.Approve, snapshot.RequestedDecisions[0]);
        Assert.Equal("scope-alpha", snapshot.EligibleReviewers[0].ScopeIds[0]);
        Assert.Equal("A redacted action summary.", snapshot.Previews[0].Detail);
        Assert.True(HumanReviewContractValidator.ValidateRequest(snapshot).IsValid);
    }

    [Fact]
    public void Request_snapshot_allocates_new_backing_storage_for_hostile_immutable_arrays()
    {
        var valid = HumanReviewTestData.Request();
        var mutableDecisions = valid.RequestedDecisions.ToArray();
        var mutableScopeIds = valid.EligibleReviewers[0].ScopeIds.ToArray();
        var mutableReviewers = new[] { valid.EligibleReviewers[0] with { ScopeIds = ImmutableCollectionsMarshal.AsImmutableArray(mutableScopeIds) } };
        var request = HumanReviewContractHash.ApplyRequest(valid with
        {
            RequestedDecisions = ImmutableCollectionsMarshal.AsImmutableArray(mutableDecisions),
            EligibleReviewers = ImmutableCollectionsMarshal.AsImmutableArray(mutableReviewers),
            RequestHash = string.Empty
        });

        Assert.True(HumanReviewContractSnapshot.TryCaptureRequest(request, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);

        mutableDecisions[0] = HumanReviewDecisionKind.Cancel;
        mutableScopeIds[0] = "scope-mutated";
        mutableReviewers[0] = new HumanReviewReviewerScope("reviewer-role-mutated", ImmutableArray.Create("scope-mutated"));

        Assert.Equal(HumanReviewDecisionKind.Approve, snapshot.RequestedDecisions[0]);
        Assert.Equal("reviewer-role-one", snapshot.EligibleReviewers[0].ReviewerRoleId);
        Assert.Equal("scope-alpha", snapshot.EligibleReviewers[0].ScopeIds[0]);
        Assert.True(HumanReviewContractValidator.ValidateRequest(snapshot).IsValid);
    }

    [Fact]
    public void Request_hash_match_rejects_missing_timing_without_throwing()
    {
        var malformed = HumanReviewTestData.Request() with { Timing = null! };

        Assert.False(HumanReviewContractHash.MatchesRequest(malformed));
        Assert.False(HumanReviewContractValidator.ValidateRequest(malformed).IsValid);
    }
}
