using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Custom;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewContinuationConsumerTests
{
    [Fact]
    public async Task Exact_approved_claim_prepares_only_the_exact_non_effect_continuation_and_completion_precondition()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.ContinuationReleasePrepared, result.Status);
        Assert.Equal(HumanReviewContinuationAction.ReleaseContinuation, result.Action?.Action);
        Assert.Equal(fixture.Run.Id, result.Action?.RunId);
        Assert.Equal(fixture.Claim.ClaimHash, result.Action?.Claim?.ClaimHash);
        Assert.Equal(fixture.Run.Id, result.Completion?.RunId);
        Assert.Equal(fixture.Run.LifecycleVersion, result.Completion?.ExpectedLifecycleVersion);
        Assert.Equal(fixture.Claim.ClaimHash, result.Completion?.Claim.ClaimHash);
        Assert.Null(result.Action?.EffectQuery);
        var receipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(result.Action?.ReleaseReceipt);
        Assert.Equal(HumanReviewContinuationReleaseKind.Continuation, receipt.Kind);
        Assert.Null(receipt.EffectReceiptHash);
        Assert.Equal(new HumanReviewRequestReference(
            Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.RequestId,
            Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.RequestHash), receipt.Request);
        Assert.Equal(receipt, result.Completion?.ReleaseReceipt);
        Assert.NotEqual(receipt.ReleaseOperationId, receipt.Claim.ClaimId);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Expired_approved_claim_keeps_a_live_wake_available_for_takeover_without_authority_or_effect_callbacks()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        Assert.True(fixture.Claim.LeaseExpiresAtUtc < Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation).Wake.ExpiresAtUtc);
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.LeaseExpiresAtUtc);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.StaleClaim, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Wake_expiry_at_the_inclusive_boundary_requests_claim_fenced_expired_retirement_without_callbacks()
    {
        var fixture = await ApprovedCandidateAsync();
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, state.Wake.ExpiresAtUtc);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Expired, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Expired, result.Retirement?.Reason);
        Assert.Equal(fixture.Run.Id, result.Retirement?.RunId);
        Assert.Equal(fixture.Run.LifecycleVersion, result.Retirement?.ExpectedLifecycleVersion);
        Assert.Equal(fixture.Claim.ClaimHash, result.Retirement?.Claim.ClaimHash);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Claim_lease_expiry_during_authority_rereads_never_retires_the_live_wake()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var clock = new HumanReviewDecisionTestClock(fixture.Claim.ClaimedAtUtc.AddSeconds(1), fixture.Claim.LeaseExpiresAtUtc);
        var consumer = Consumer(authority, effectEvidence, effectCertainty, clock);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.StaleClaim, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, clock.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Wake_expiry_during_authority_rereads_requests_only_claim_fenced_expired_retirement()
    {
        var fixture = await ApprovedCandidateAsync();
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var clock = new HumanReviewDecisionTestClock(fixture.Claim.ClaimedAtUtc.AddSeconds(1), state.Wake.ExpiresAtUtc);
        var consumer = Consumer(authority, new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), clock);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Expired, result.Retirement?.Outcome);
        Assert.Equal(fixture.Claim.ClaimHash, result.Retirement?.Claim.ClaimHash);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, clock.ReadCount);
    }

    [Fact]
    public async Task Trusted_time_rollback_after_authority_reread_fails_closed_without_release_or_retirement()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var clock = new HumanReviewDecisionTestClock(fixture.Claim.ClaimedAtUtc.AddSeconds(1), fixture.Claim.ClaimedAtUtc.AddTicks(-1));
        var consumer = Consumer(authority, new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), clock);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, clock.ReadCount);
    }

    [Fact]
    public async Task Partial_trusted_time_rollback_during_non_effect_rereads_fails_closed_without_release_or_retirement()
    {
        var fixture = await ApprovedCandidateAsync();
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var reservation = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).ContinuationReservation;
        var initialObservedAtUtc = fixture.Claim.ClaimedAtUtc.AddSeconds(40);
        var finalObservedAtUtc = fixture.Claim.ClaimedAtUtc.AddSeconds(20);
        Assert.True(finalObservedAtUtc > fixture.Run.UpdatedAtUtc);
        Assert.True(finalObservedAtUtc > Assert.IsType<HumanReviewContinuationReservation>(reservation).ReservedAtUtc);
        Assert.True(finalObservedAtUtc > state.Wake.PublishedAtUtc);
        Assert.True(finalObservedAtUtc > fixture.Claim.ClaimedAtUtc);
        Assert.True(finalObservedAtUtc < initialObservedAtUtc);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var clock = new HumanReviewDecisionTestClock(initialObservedAtUtc, finalObservedAtUtc);
        var consumer = Consumer(authority, new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), clock);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, clock.ReadCount);
    }

    [Fact]
    public async Task Partial_trusted_time_rollback_during_effect_rereads_fails_closed_without_release_or_retirement()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var reservation = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).ContinuationReservation;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var initialObservedAtUtc = fixture.Claim.ClaimedAtUtc.AddSeconds(40);
        var finalObservedAtUtc = fixture.Claim.ClaimedAtUtc.AddSeconds(20);
        Assert.True(finalObservedAtUtc > fixture.Run.UpdatedAtUtc);
        Assert.True(finalObservedAtUtc > Assert.IsType<HumanReviewContinuationReservation>(reservation).ReservedAtUtc);
        Assert.True(finalObservedAtUtc > state.Wake.PublishedAtUtc);
        Assert.True(finalObservedAtUtc > fixture.Claim.ClaimedAtUtc);
        Assert.True(finalObservedAtUtc < initialObservedAtUtc);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence));
        var effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(snapshot), CurrentSnapshot(snapshot));
        var clock = new HumanReviewDecisionTestClock(initialObservedAtUtc, finalObservedAtUtc);
        var consumer = Consumer(authority, effectEvidence, effectCertainty, clock);

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
        Assert.Equal(2, clock.ReadCount);
    }

    [Theory]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Narrowed)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Revoked)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Stale)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Invalid)]
    public async Task Noncurrent_authority_requests_blocked_retirement_without_release(HumanReviewContinuationAuthorityReadStatus authorityStatus)
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(authorityStatus);
        var consumer = Consumer(authority, new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Blocked, result.Retirement?.Reason);
        Assert.Equal(fixture.Run.Id, result.Retirement?.RunId);
        Assert.Equal(fixture.Run.LifecycleVersion, result.Retirement?.ExpectedLifecycleVersion);
        Assert.Equal(fixture.Claim.ClaimHash, result.Retirement?.Claim.ClaimHash);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
    }

    [Theory]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Narrowed, HumanReviewContinuationConsumptionStatus.RetirementRequired)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Revoked, HumanReviewContinuationConsumptionStatus.RetirementRequired)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Stale, HumanReviewContinuationConsumptionStatus.RetirementRequired)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Invalid, HumanReviewContinuationConsumptionStatus.RetirementRequired)]
    [InlineData(HumanReviewContinuationAuthorityReadStatus.Unavailable, HumanReviewContinuationConsumptionStatus.Unavailable)]
    public async Task Authority_that_changes_after_the_initial_reread_never_releases_the_claim(HumanReviewContinuationAuthorityReadStatus finalStatus, HumanReviewContinuationConsumptionStatus expectedStatus)
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, finalStatus);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(expectedStatus == HumanReviewContinuationConsumptionStatus.RetirementRequired ? HumanReviewContinuationOutcome.Blocked : null, result.Retirement?.Outcome);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewContinuationAction.FailRejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewContinuationAction.Cancel)]
    [InlineData(HumanReviewDecisionKind.RequestInformation, HumanReviewContinuationAction.ParkForInformation)]
    public async Task Nonapproval_decisions_prepare_only_their_distinct_declared_paths(HumanReviewDecisionKind decisionKind, HumanReviewContinuationAction expectedAction)
    {
        var candidate = await DecisionCandidateAsync(decisionKind);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, candidate.Run.UpdatedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.DecisionPathPrepared, result.Status);
        Assert.Equal(expectedAction, result.Action?.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewContinuationAction.FailRejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewContinuationAction.Cancel)]
    [InlineData(HumanReviewDecisionKind.RequestInformation, HumanReviewContinuationAction.ParkForInformation)]
    public async Task Exact_nonapproval_action_consumption_uses_only_the_supplied_accepted_decision_without_approval_or_effect_reads(HumanReviewDecisionKind decisionKind, HumanReviewContinuationAction expectedAction)
    {
        var candidate = await DecisionCandidateAsync(decisionKind);
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(candidate.Run.HumanReview);
        var decision = Assert.Single(review.AcceptedDecisions);
        var reference = new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, candidate.Run.UpdatedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeDecisionActionAsync(candidate, reference);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.DecisionPathPrepared, result.Status);
        Assert.Equal(expectedAction, result.Action?.Action);
        Assert.Equal(reference, result.Action?.Decision);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Exact_nonapproval_action_consumption_rejects_a_superseded_action_head_without_any_port_callback()
    {
        var initial = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(initial.Run);
        var service = new HumanReviewDecisionService(store, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(initial.Run.UpdatedAtUtc.AddMinutes(1), initial.Run.UpdatedAtUtc.AddMinutes(2)));
        Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, (await service.DecideAsync(HumanReviewDecisionTestData.Command(initial.Run, "stale-action-information", HumanReviewDecisionKind.RequestInformation, "Need a bounded clarification."))).Status);
        var informationRun = Assert.IsType<CustomLoopRunRecord>(store.Run);
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, (await service.DecideAsync(HumanReviewDecisionTestData.Command(informationRun, "stale-action-reject", HumanReviewDecisionKind.Reject))).Status);
        var currentRun = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(currentRun.HumanReview);
        var superseded = review.AcceptedDecisions[0];
        var reference = new HumanReviewDecisionReference(superseded.DecisionId, superseded.DecisionOperationId, superseded.Kind, superseded.DecisionHash);
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, currentRun.UpdatedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeDecisionActionAsync(new(currentRun, context.Artifact, null, null), reference);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Invalid, result.Status);
        Assert.Null(result.Action);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Missing_or_mismatched_claim_fails_closed_without_any_port_callback()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var candidate = fixture.Candidate with { Claim = fixture.Claim with { ClaimId = "different-claim" } };
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Invalid, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Invalid_candidate_fails_closed_before_any_authority_or_effect_read()
    {
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var result = await Consumer(authority, effectEvidence, effectCertainty, DateTimeOffset.UtcNow).ConsumeAsync(new HumanReviewContinuationCandidate(null!, null, null, null));

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Invalid, result.Status);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Malformed_decision_action_ledger_fails_closed_during_canonical_context_capture()
    {
        var fixture = await ApprovedCandidateAsync();
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var malformedAction = new HumanReviewDecisionActionState(1, null!, Hash('a'), 1, fixture.Run.LifecycleVersion, null, ImmutableArray<HumanReviewDecisionActionClaim>.Empty, null, null, Hash('b'));
        var malformedRun = fixture.Run with { HumanReview = review with { DecisionActions = ImmutableArray.Create(malformedAction) } };
        var candidate = fixture.Candidate with { Run = malformedRun };
        var result = await Consumer(new RecordingAuthoritySource(), new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Invalid, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
    }

    [Fact]
    public async Task Malformed_nested_run_artifacts_fail_closed_during_context_capture()
    {
        var fixture = await ApprovedCandidateAsync();
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var malformedRuns = new[]
        {
            fixture.Run with { ExecutionClock = null! },
            fixture.Run with { Checkpoint = null! },
            fixture.Run with { ContextSnapshot = null! },
            fixture.Run with { AdmittedDefinition = null! },
            fixture.Run with { CapabilityAdmission = null! },
            fixture.Run with { SequentialInvocationSnapshot = null! },
            fixture.Run with { SequentialAdapterBinding = null! },
            fixture.Run with { Frontier = null! },
            fixture.Run with { HumanReview = review with { Lifecycle = null! } },
            fixture.Run with { HumanReview = review with { LifecycleHistory = default } },
            fixture.Run with { HumanReview = review with { OperationReceipts = default } },
            fixture.Run with { HumanReview = review with { AcceptedDecisions = default } },
        };

        foreach (var malformedRun in malformedRuns)
        {
            var result = await Consumer(new RecordingAuthoritySource(), new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate with { Run = malformedRun });
            Assert.Equal(HumanReviewContinuationConsumptionStatus.Invalid, result.Status);
            Assert.Null(result.Action);
            Assert.Null(result.Completion);
            Assert.Null(result.Retirement);
        }
    }

    [Fact]
    public async Task First_authority_unavailable_keeps_an_approved_claim_parked_without_retirement()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Unavailable);
        var result = await Consumer(authority, new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(1, authority.ReadCount);
    }

    [Fact]
    public async Task Authority_source_failure_is_projected_as_unavailable_without_release_or_retirement()
    {
        var fixture = await ApprovedCandidateAsync();
        var result = await Consumer(new ThrowingHumanReviewContinuationAuthoritySource(new InvalidOperationException("authority unavailable")), new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
    }

    [Fact]
    public async Task Final_authority_unavailable_after_an_exact_effect_read_keeps_the_claim_parked()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Unavailable);
        var result = await Consumer(authority, new RecordingEffectEvidenceSource(CurrentEvidence(evidence)), new RecordingEffectCertaintySource(CurrentSnapshot(snapshot)), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
    }

    [Fact]
    public async Task Final_narrowed_authority_after_an_exact_effect_read_requires_blocked_retirement()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Narrowed);
        var result = await Consumer(authority, new RecordingEffectEvidenceSource(CurrentEvidence(evidence)), new RecordingEffectCertaintySource(CurrentSnapshot(snapshot)), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Blocked, result.Retirement?.Reason);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(2, authority.ReadCount);
    }

    [Fact]
    public async Task Final_effect_certainty_unavailable_after_the_first_exact_read_keeps_the_claim_parked()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(snapshot), new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Unavailable));
        var result = await Consumer(authority, new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence)), effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Effect_evidence_source_failure_is_projected_as_unavailable_without_certainty_lookup()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var result = await Consumer(authority, new ThrowingHumanReviewCurrentEffectAttemptEvidenceSource(new IOException("effect evidence unavailable")), new RecordingEffectCertaintySource(), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(1, authority.ReadCount);
    }

    [Fact]
    public async Task Effect_certainty_source_failure_is_projected_as_unavailable_without_release_or_retirement()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(EffectEvidence(binding, effectAttempt)));
        var result = await Consumer(authority, effectEvidence, new ThrowingHumanReviewEffectCertaintySnapshotSource(new IOException("certainty unavailable")), fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
    }

    [Fact]
    public async Task Invalid_effect_evidence_status_requires_blocked_retirement_without_certainty_lookup()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(new HumanReviewCurrentEffectAttemptEvidenceReadResult((HumanReviewCurrentEffectAttemptEvidenceReadStatus)999));
        var effectCertainty = new RecordingEffectCertaintySource();
        var result = await Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Blocked, result.Retirement?.Reason);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Malformed_current_effect_evidence_fails_closed_before_a_certainty_lookup()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var malformed = new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new HumanReviewCurrentEffectAttemptEvidence(null!, null!));
        var effectEvidence = new RecordingEffectEvidenceSource(malformed);
        var effectCertainty = new RecordingEffectCertaintySource();
        var result = await Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Invalid_trusted_time_keeps_a_nonapproval_decision_parked()
    {
        var candidate = await DecisionCandidateAsync(HumanReviewDecisionKind.Reject);
        var result = await Consumer(new RecordingAuthoritySource(), new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), new HumanReviewDecisionTestClock(default(DateTimeOffset))).ConsumeAsync(candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
    }

    [Fact]
    public async Task Trusted_clock_failure_keeps_a_nonapproval_decision_parked()
    {
        var candidate = await DecisionCandidateAsync(HumanReviewDecisionKind.Reject);
        var result = await Consumer(new RecordingAuthoritySource(), new RecordingEffectEvidenceSource(), new RecordingEffectCertaintySource(), new ThrowingHumanReviewTrustedClock(new InvalidOperationException("clock unavailable"))).ConsumeAsync(candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
    }

    [Fact]
    public async Task Exact_approved_effect_claim_prepares_only_an_exact_not_started_effect_release_and_completion_precondition()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var executionBinding = Assert.IsType<GovernedLoopSequentialAdapterBinding>(fixture.Run.SequentialAdapterBinding).ExecutionBinding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(
            CurrentEvidence(evidence),
            CurrentEvidence(evidence));
        var effectCertainty = new RecordingEffectCertaintySource(
            CurrentSnapshot(snapshot),
            CurrentSnapshot(snapshot));
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.EffectReleasePrepared, result.Status);
        Assert.Equal(HumanReviewContinuationAction.ReleaseEffect, result.Action?.Action);
        Assert.Equal(new GovernedLoopEffectCertaintySnapshotQuery(evidence.Identity, evidence.Preparation), result.Action?.EffectQuery);
        Assert.Equal(binding.WorkspaceId, evidence.Identity.WorkspaceId);
        Assert.Equal(executionBinding.ExecutionGeneration, evidence.Identity.ExecutionGeneration);
        Assert.Equal(fixture.Run.Id, result.Completion?.RunId);
        Assert.Equal(fixture.Run.LifecycleVersion, result.Completion?.ExpectedLifecycleVersion);
        Assert.Equal(fixture.Claim.ClaimHash, result.Completion?.Claim.ClaimHash);
        var receipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(result.Action?.ReleaseReceipt);
        Assert.Equal(HumanReviewContinuationReleaseKind.PreDispatchEffect, receipt.Kind);
        Assert.Equal(snapshot.SnapshotHash, receipt.EffectReceiptHash);
        Assert.Equal(receipt, result.Completion?.ReleaseReceipt);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
        Assert.All(effectEvidence.Queries, query =>
        {
            Assert.Equal(binding, query.Binding);
            Assert.Equal(binding.EffectAttempt, query.EffectAttempt);
        });
    }

    [Fact]
    public async Task Ordered_release_rejects_a_changed_exact_not_started_effect_snapshot_before_any_transition_or_runtime_handoff()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var originalState = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var publishedState = HumanReviewContinuationContractHash.ApplyState(originalState with { Claims = ImmutableArray<HumanReviewContinuationClaim>.Empty, StateHash = string.Empty });
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var published = fixture.Run with
        {
            LifecycleVersion = fixture.Run.LifecycleVersion + 1,
            UpdatedAtUtc = originalState.Wake.PublishedAtUtc,
            HumanReview = review with { Continuation = publishedState },
        };
        Assert.NotNull((await store.UpdateAsync(published, fixture.Run.LifecycleVersion)).Run);
        var claimed = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            UpdatedAtUtc = fixture.Claim.ClaimedAtUtc,
            HumanReview = published.HumanReview! with { Continuation = originalState },
        };
        Assert.NotNull((await store.UpdateAsync(claimed, published.LifecycleVersion)).Run);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(review.Request, reservation, originalState).IsValid);

        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(review.Request.Binding, effectAttempt);
        var expectedSnapshot = HumanReviewEffectReleaseContract.Create(review.Request.Binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var prepared = await Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence)),
            new RecordingEffectCertaintySource(CurrentSnapshot(expectedSnapshot), CurrentSnapshot(expectedSnapshot)),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1)).ConsumeAsync(new HumanReviewContinuationCandidate(claimed, fixture.Candidate.GraphArtifact, originalState, fixture.Claim));
        Assert.Equal(HumanReviewContinuationConsumptionStatus.EffectReleasePrepared, prepared.Status);
        var action = Assert.IsType<HumanReviewContinuationActionIntent>(prepared.Action);
        var completion = Assert.IsType<HumanReviewContinuationCompletionIntent>(prepared.Completion);
        var receipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(action.ReleaseReceipt);
        var changedSnapshot = HumanReviewEffectReleaseContract.Create(review.Request.Binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(2));
        Assert.NotEqual(receipt.EffectReceiptHash, changedSnapshot.SnapshotHash);

        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        Assert.Equal(claimed.SequentialAdapterBinding?.GraphArtifactHash, context.Artifact.ArtifactHash);
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(GovernedLoopSequentialRunAnchorGuard.Create(
            context.AdapterBinding,
            context.AdmissionRequest,
            context.Receipt,
            context.Invocation,
            context.Artifact).Anchor);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var releaseEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(evidence));
        var releaseCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(changedSnapshot));
        var runtime = new HumanReviewOrderedReleaseTestRuntime();
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(anchor, context.Plan, context.Artifact)),
            runtime,
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(fixture.Claim.ClaimedAtUtc.AddSeconds(2)),
            authority,
            releaseEvidence,
            releaseCertainty);
        var retained = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var updatesBeforeRelease = store.UpdateCount;
        var lifecycleBeforeRelease = retained.LifecycleVersion;
        var eventCountBeforeRelease = retained.Events.Length;

        var result = await release.ReleaseAsync(action, completion);

        Assert.Equal(HumanReviewContinuationReleaseStatus.Invalid, result.Status);
        Assert.Equal(updatesBeforeRelease, store.UpdateCount);
        Assert.Equal(lifecycleBeforeRelease, store.Run?.LifecycleVersion);
        Assert.Equal(eventCountBeforeRelease, store.Run?.Events.Length);
        Assert.Equal(retained, store.Run);
        Assert.Equal(1, releaseEvidence.ReadCount);
        Assert.Equal(1, releaseCertainty.ReadCount);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, runtime.ResumeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Well_formed_effect_evidence_from_another_workspace_or_execution_generation_fails_closed(bool differentWorkspace)
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var alternateWorkspaceId = binding.WorkspaceId[..^1] + (binding.WorkspaceId[^1] == 'a' ? "b" : "a");
        var alternateGeneration = evidence.Identity.ExecutionGeneration == 1 ? 2 : 1;
        var changedIdentity = evidence.Identity with
        {
            WorkspaceId = differentWorkspace ? alternateWorkspaceId : evidence.Identity.WorkspaceId,
            ExecutionGeneration = differentWorkspace ? evidence.Identity.ExecutionGeneration : alternateGeneration,
            IdentityHash = string.Empty,
        };
        changedIdentity = changedIdentity with { IdentityHash = HumanReviewEffectReleaseContract.ComputeIdentity(changedIdentity) };
        var changedEvidence = evidence with { Identity = changedIdentity };
        Assert.True(HumanReviewEffectReleaseContract.TryCaptureExpectation(changedEvidence.Identity, changedEvidence.Preparation, out _, out _, out _));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(changedEvidence));
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Pre_dispatch_completion_factory_binds_the_final_certainty_receipt_and_fails_closed_on_replay_or_receipt_tampering()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var binding = review.Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(2));
        var consumer = Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence)),
            new RecordingEffectCertaintySource(CurrentSnapshot(snapshot), CurrentSnapshot(snapshot)),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var consumed = await consumer.ConsumeAsync(fixture.Candidate);
        var intent = Assert.IsType<HumanReviewContinuationCompletionIntent>(consumed.Completion);
        var completedAtUtc = fixture.Claim.ClaimedAtUtc.AddSeconds(2);

        var created = HumanReviewContinuationCompletionIntentFactory.TryCreate(
            intent,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            "completion-continuation-one",
            Hash('b'),
            Hash('c'),
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("completion-correlation", completedAtUtc),
            out var completion);

        Assert.True(created);
        var actual = Assert.IsType<HumanReviewContinuationCompletion>(completion);
        Assert.Equal(HumanReviewContinuationReleaseKind.PreDispatchEffect, actual.ReleaseReceipt.Kind);
        Assert.Equal(snapshot.SnapshotHash, actual.ReleaseReceipt.EffectReceiptHash);
        Assert.True(HumanReviewContinuationContractValidator.ValidateReleaseReceipt(review.Request, state.Wake, reservation, fixture.Claim, actual.ReleaseReceipt).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateCompletion(review.Request, state.Wake, reservation, fixture.Claim, actual).IsValid);

        var tampered = intent with { ReleaseReceipt = intent.ReleaseReceipt with { EffectReceiptHash = null } };
        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            tampered,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            "completion-continuation-two",
            Hash('b'),
            Hash('c'),
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("tampered-completion", completedAtUtc),
            out var tamperedCompletion));
        Assert.Null(tamperedCompletion);

        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            intent,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            intent.ReleaseReceipt.ReleaseOperationId,
            Hash('b'),
            Hash('c'),
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("replayed-completion", completedAtUtc),
            out var replayedCompletion));
        Assert.Null(replayedCompletion);
    }

    [Fact]
    public async Task Completion_factory_requires_a_completion_time_strictly_before_the_claim_lease_expiry()
    {
        var fixture = await ApprovedCandidateAsync();
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var consumer = Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(),
            new RecordingEffectCertaintySource(),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        var intent = Assert.IsType<HumanReviewContinuationCompletionIntent>((await consumer.ConsumeAsync(fixture.Candidate)).Completion);
        var beforeExpiry = fixture.Claim.LeaseExpiresAtUtc.AddTicks(-1);
        var atExpiry = fixture.Claim.LeaseExpiresAtUtc;
        var afterExpiry = fixture.Claim.LeaseExpiresAtUtc.AddTicks(1);

        Assert.True(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            intent,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            "completion-before-expiry",
            Hash('b'),
            Hash('c'),
            beforeExpiry,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("completion-before-expiry", beforeExpiry),
            out var beforeExpiryCompletion));
        Assert.NotNull(beforeExpiryCompletion);

        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            intent,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            "completion-at-expiry",
            Hash('b'),
            Hash('c'),
            atExpiry,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("completion-at-expiry", atExpiry),
            out var atExpiryCompletion));
        Assert.Null(atExpiryCompletion);

        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            intent,
            review.Request,
            state.Wake,
            reservation,
            fixture.Claim,
            "completion-after-expiry",
            Hash('b'),
            Hash('c'),
            afterExpiry,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("completion-after-expiry", afterExpiry),
            out var afterExpiryCompletion));
        Assert.Null(afterExpiryCompletion);
    }

    [Fact]
    public async Task Completion_factory_rejects_a_mixed_canonical_request_reservation_wake_and_claim_chain()
    {
        var fixture = await ApprovedCandidateAsync();
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var state = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var consumer = Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(),
            new RecordingEffectCertaintySource(),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        var intent = Assert.IsType<HumanReviewContinuationCompletionIntent>((await consumer.ConsumeAsync(fixture.Candidate)).Completion);
        var alternateRequest = HumanReviewContractHash.ApplyRequest(review.Request with
        {
            RequestId = "review-request-two",
            RequestOperationId = "review-request-operation-two",
            RequestHash = string.Empty,
        });
        var alternateRequestReference = new HumanReviewRequestReference(alternateRequest.RequestId, alternateRequest.RequestHash);
        var accepted = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var alternateDecision = HumanReviewContractHash.ApplyDecision(accepted with
        {
            DecisionId = "decision-continuation-two",
            DecisionOperationId = "approve-continuation-two",
            Request = alternateRequestReference,
            DecisionHash = string.Empty,
        });
        var alternateReservation = HumanReviewContractHash.ApplyContinuationReservation(reservation with
        {
            ReservationId = "reservation-continuation-two",
            Request = alternateRequestReference,
            Decision = new HumanReviewDecisionReference(alternateDecision.DecisionId, alternateDecision.DecisionOperationId, alternateDecision.Kind, alternateDecision.DecisionHash),
            ReservationHash = string.Empty,
        });
        var alternateWake = HumanReviewContinuationContractHash.ApplyWake(state.Wake with
        {
            WakeId = "wake-continuation-two",
            Request = alternateRequestReference,
            Decision = new HumanReviewDecisionReference(alternateDecision.DecisionId, alternateDecision.DecisionOperationId, alternateDecision.Kind, alternateDecision.DecisionHash),
            Reservation = new HumanReviewContinuationReservationReference(alternateReservation.ReservationId, alternateReservation.ReservationHash),
            WakeHash = string.Empty,
        });
        var alternateClaim = HumanReviewContinuationContractHash.ApplyClaim(fixture.Claim with
        {
            ClaimId = "claim-continuation-two",
            Wake = new HumanReviewContinuationWakeReference(alternateWake.WakeId, alternateWake.WakeHash),
            Reservation = new HumanReviewContinuationReservationReference(alternateReservation.ReservationId, alternateReservation.ReservationHash),
            ClaimHash = string.Empty,
        });
        Assert.True(HumanReviewContractValidator.ValidateRequest(alternateRequest).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateDecision(alternateRequest, alternateDecision).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateContinuationReservation(alternateRequest, alternateReservation).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateWake(alternateRequest, alternateReservation, alternateWake).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateClaim(alternateWake, alternateReservation, alternateClaim).IsValid);

        var mixed = intent with
        {
            Wake = new HumanReviewContinuationWakeReference(alternateWake.WakeId, alternateWake.WakeHash),
            Claim = new HumanReviewContinuationClaimReference(alternateClaim.ClaimId, alternateClaim.ClaimHash),
            ReleaseReceipt = intent.ReleaseReceipt with
            {
                Wake = new HumanReviewContinuationWakeReference(alternateWake.WakeId, alternateWake.WakeHash),
                Claim = new HumanReviewContinuationClaimReference(alternateClaim.ClaimId, alternateClaim.ClaimHash),
            },
        };
        var completedAtUtc = alternateClaim.ClaimedAtUtc.AddSeconds(2);

        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            mixed,
            review.Request,
            alternateWake,
            reservation,
            alternateClaim,
            "completion-mixed-chain-one",
            Hash('b'),
            Hash('c'),
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("mixed-chain-completion", completedAtUtc),
            out var completion));
        Assert.Null(completion);
    }

    [Fact]
    public async Task Effect_certainty_drift_during_the_final_reread_blocks_release_with_a_claim_fenced_retirement()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var safeSnapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var dispatchedSnapshot = SnapshotFor(binding, effectAttempt, HumanReviewEffectCertainty.Dispatched);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence));
        var effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(safeSnapshot), CurrentSnapshot(dispatchedSnapshot));
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Equal(fixture.Run.Id, result.Retirement?.RunId);
        Assert.Equal(fixture.Run.LifecycleVersion, result.Retirement?.ExpectedLifecycleVersion);
        Assert.Equal(fixture.Claim.ClaimHash, result.Retirement?.Claim.ClaimHash);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Effect_release_does_not_consume_a_third_unpaired_effect_evidence_drift()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var safeSnapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(
            CurrentEvidence(evidence),
            CurrentEvidence(evidence),
            new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale));
        var effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(safeSnapshot), CurrentSnapshot(safeSnapshot));
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.EffectReleasePrepared, result.Status);
        Assert.Equal(new GovernedLoopEffectCertaintySnapshotQuery(evidence.Identity, evidence.Preparation), result.Action?.EffectQuery);
        Assert.Equal(fixture.Claim.ClaimHash, result.Completion?.Claim.ClaimHash);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Recovery_requeries_effect_certainty_and_keeps_the_preallocated_operation_identity_when_the_observation_hash_changes()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var firstSnapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var finalSnapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(2));
        var recoveredSnapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(3));
        var firstConsumer = Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence)),
            new RecordingEffectCertaintySource(CurrentSnapshot(firstSnapshot), CurrentSnapshot(finalSnapshot)),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        var recoveredConsumer = Consumer(
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current),
            new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence)),
            new RecordingEffectCertaintySource(CurrentSnapshot(recoveredSnapshot), CurrentSnapshot(recoveredSnapshot)),
            fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var first = await firstConsumer.ConsumeAsync(fixture.Candidate);
        var recovered = await recoveredConsumer.ConsumeAsync(fixture.Candidate);

        var firstReceipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(first.Action?.ReleaseReceipt);
        var recoveredReceipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(recovered.Action?.ReleaseReceipt);
        Assert.Equal(HumanReviewContinuationConsumptionStatus.EffectReleasePrepared, first.Status);
        Assert.Equal(HumanReviewContinuationConsumptionStatus.EffectReleasePrepared, recovered.Status);
        Assert.Equal(firstReceipt.ReleaseOperationId, recoveredReceipt.ReleaseOperationId);
        Assert.Equal(finalSnapshot.SnapshotHash, firstReceipt.EffectReceiptHash);
        Assert.Equal(recoveredSnapshot.SnapshotHash, recoveredReceipt.EffectReceiptHash);
        Assert.NotEqual(firstReceipt.EffectReceiptHash, recoveredReceipt.EffectReceiptHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_loss_takeover_reuses_the_release_operation_identity_but_fences_the_expired_claim(bool includeEffectAttempt)
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt);
        var first = await ConsumeApprovedAsync(fixture.Candidate, fixture.Claim.ClaimedAtUtc.AddSeconds(1), fixture.EffectAttempt);
        var takeover = TakeoverCandidate(fixture, out var successorClaim);
        var recovered = await ConsumeApprovedAsync(takeover, successorClaim.ClaimedAtUtc.AddSeconds(1), fixture.EffectAttempt);

        var firstReceipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(first.Action?.ReleaseReceipt);
        var recoveredReceipt = Assert.IsType<HumanReviewContinuationReleaseReceiptIntent>(recovered.Action?.ReleaseReceipt);
        Assert.Equal(includeEffectAttempt ? HumanReviewContinuationConsumptionStatus.EffectReleasePrepared : HumanReviewContinuationConsumptionStatus.ContinuationReleasePrepared, first.Status);
        Assert.Equal(first.Status, recovered.Status);
        Assert.Equal(firstReceipt.ReleaseOperationId, recoveredReceipt.ReleaseOperationId);
        Assert.NotEqual(firstReceipt.Claim, recoveredReceipt.Claim);
        Assert.Equal(new HumanReviewContinuationClaimReference(successorClaim.ClaimId, successorClaim.ClaimHash), recoveredReceipt.Claim);

        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Assert.IsType<HumanReviewContinuationState>(takeover.Continuation).Wake;
        Assert.False(HumanReviewContinuationCompletionIntentFactory.TryCreate(
            Assert.IsType<HumanReviewContinuationCompletionIntent>(first.Completion),
            review.Request,
            wake,
            reservation,
            successorClaim,
            "completion-stale-claim-one",
            Hash('b'),
            Hash('c'),
            successorClaim.ClaimedAtUtc.AddSeconds(1),
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance("stale-claim-completion", successorClaim.ClaimedAtUtc.AddSeconds(1)),
            out var staleCompletion));
        Assert.Null(staleCompletion);
    }

    [Theory]
    [InlineData(HumanReviewEffectCertainty.Dispatched)]
    [InlineData(HumanReviewEffectCertainty.Conclusive)]
    [InlineData(HumanReviewEffectCertainty.Ambiguous)]
    [InlineData(HumanReviewEffectCertainty.Terminal)]
    public async Task Effect_certainty_that_is_not_exactly_not_started_requests_blocked_retirement(HumanReviewEffectCertainty certainty)
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(evidence));
        var effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(SnapshotFor(binding, effectAttempt, certainty)));
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Blocked, result.Retirement?.Reason);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(1, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Unavailable_effect_evidence_keeps_an_approved_effect_claim_parked_without_retirement()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.Unavailable, result.Status);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Null(result.Retirement);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Theory]
    [InlineData(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unknown)]
    [InlineData(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Missing)]
    [InlineData(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt)]
    [InlineData(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale)]
    public async Task Noncurrent_effect_evidence_requests_blocked_retirement_without_certainty_lookup(HumanReviewCurrentEffectAttemptEvidenceReadStatus evidenceStatus)
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(new HumanReviewCurrentEffectAttemptEvidenceReadResult(evidenceStatus));
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, result.Retirement?.Outcome);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_before_evaluation_prevents_all_port_reads()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(fixture.Candidate, cancellation.Token));

        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_during_an_unavailable_authority_reread_takes_precedence_over_unavailable()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Unavailable);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        authority.AfterRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(fixture.Candidate, cancellation.Token));

        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_during_unavailable_effect_evidence_takes_precedence_over_unavailable()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        effectEvidence.AfterRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(fixture.Candidate, cancellation.Token));

        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_during_unavailable_effect_certainty_takes_precedence_over_unavailable()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(EffectEvidence(binding, effectAttempt)));
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        effectCertainty.AfterRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(fixture.Candidate, cancellation.Token));

        Assert.Equal(1, authority.ReadCount);
        Assert.Equal(1, effectEvidence.ReadCount);
        Assert.Equal(1, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_immediately_before_a_nonapproval_action_prevents_the_action_intent()
    {
        var candidate = await DecisionCandidateAsync(HumanReviewDecisionKind.Reject);
        var authority = new RecordingAuthoritySource();
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var clock = new HumanReviewDecisionTestClock(candidate.Run.UpdatedAtUtc.AddSeconds(1));
        var consumer = Consumer(authority, effectEvidence, effectCertainty, clock);
        using var cancellation = new CancellationTokenSource();
        clock.AfterRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(candidate, cancellation.Token));

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Cancellation_immediately_before_release_prevents_the_action_and_completion_intents()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.ClaimedAtUtc.AddSeconds(1));
        using var cancellation = new CancellationTokenSource();
        authority.AfterRead = count =>
        {
            if (count == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ConsumeAsync(fixture.Candidate, cancellation.Token));

        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    private static HumanReviewContinuationConsumer Consumer(
        RecordingAuthoritySource authority,
        RecordingEffectEvidenceSource effectEvidence,
        RecordingEffectCertaintySource effectCertainty,
        DateTimeOffset now)
        => new(authority, effectEvidence, effectCertainty, new HumanReviewDecisionTestClock(now));

    private static HumanReviewContinuationConsumer Consumer(
        IHumanReviewContinuationAuthoritySource authority,
        IHumanReviewCurrentEffectAttemptEvidenceSource effectEvidence,
        IGovernedLoopEffectCertaintySnapshotSource effectCertainty,
        DateTimeOffset now)
        => new(authority, effectEvidence, effectCertainty, new HumanReviewDecisionTestClock(now));

    private static HumanReviewContinuationConsumer Consumer(
        IHumanReviewContinuationAuthoritySource authority,
        IHumanReviewCurrentEffectAttemptEvidenceSource effectEvidence,
        IGovernedLoopEffectCertaintySnapshotSource effectCertainty,
        IHumanReviewTrustedClock clock)
        => new(authority, effectEvidence, effectCertainty, clock);

    private static HumanReviewContinuationConsumer Consumer(
        RecordingAuthoritySource authority,
        RecordingEffectEvidenceSource effectEvidence,
        RecordingEffectCertaintySource effectCertainty,
        IHumanReviewTrustedClock clock)
        => new(authority, effectEvidence, effectCertainty, clock);

    private static async Task<HumanReviewContinuationConsumptionResult> ConsumeApprovedAsync(HumanReviewContinuationCandidate candidate, DateTimeOffset now, GovernedLoopEffectAttempt? effectAttempt)
    {
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        if (effectAttempt is not null)
        {
            var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(candidate.Run.HumanReview).Request.Binding;
            var evidence = EffectEvidence(binding, effectAttempt);
            var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
            effectEvidence = new RecordingEffectEvidenceSource(CurrentEvidence(evidence), CurrentEvidence(evidence));
            effectCertainty = new RecordingEffectCertaintySource(CurrentSnapshot(snapshot), CurrentSnapshot(snapshot));
        }

        return await Consumer(authority, effectEvidence, effectCertainty, now).ConsumeAsync(candidate);
    }

    private static HumanReviewContinuationCandidate TakeoverCandidate(ApprovedCandidateFixture fixture, out HumanReviewContinuationClaim successorClaim)
    {
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var continuation = Assert.IsType<HumanReviewContinuationState>(fixture.Candidate.Continuation);
        var claimedAtUtc = fixture.Claim.LeaseExpiresAtUtc.AddTicks(1);
        successorClaim = HumanReviewContinuationContractHash.ApplyClaim(fixture.Claim with
        {
            ClaimId = "claim-continuation-takeover",
            WorkerId = "worker-continuation-takeover",
            ClaimedAtUtc = claimedAtUtc,
            LeaseExpiresAtUtc = claimedAtUtc.AddMinutes(2),
            Provenance = Provenance("claim-takeover", claimedAtUtc),
            ClaimHash = string.Empty,
        });
        var successorState = HumanReviewContinuationContractHash.ApplyState(continuation with
        {
            Claims = [fixture.Claim, successorClaim],
            StateHash = string.Empty,
        });
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(review.Request, reservation, successorState).IsValid);
        return new HumanReviewContinuationCandidate(fixture.Run, fixture.Candidate.GraphArtifact, successorState, successorClaim);
    }

    private static async Task<ApprovedCandidateFixture> ApprovedCandidateAsync(bool includeEffectAttempt = false)
    {
        var initial = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: includeEffectAttempt);
        var store = new HumanReviewDecisionTestStore(initial.Run);
        var decisionAtUtc = initial.Run.UpdatedAtUtc.AddMinutes(1);
        var decision = new HumanReviewDecisionService(store, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(decisionAtUtc));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, (await decision.DecideAsync(HumanReviewDecisionTestData.Command(initial.Run, "approve-continuation-one", HumanReviewDecisionKind.Approve))).Status);
        var run = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var review = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(run.HumanReview);
        var request = review.Request;
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var accepted = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var wakeAtUtc = run.UpdatedAtUtc.AddSeconds(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            1,
            "wake-continuation-one",
            new HumanReviewRequestReference(request.RequestId, request.RequestHash),
            new HumanReviewDecisionReference(accepted.DecisionId, accepted.DecisionOperationId, accepted.Kind, accepted.DecisionHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            request.Binding.BindingHash,
            1,
            wakeAtUtc,
            wakeAtUtc.AddMinutes(5),
            Provenance("wake-correlation", wakeAtUtc),
            string.Empty));
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            "claim-continuation-one",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "worker-continuation-one",
            wakeAtUtc.AddSeconds(1),
            wakeAtUtc.AddMinutes(2),
            Provenance("claim-correlation", wakeAtUtc.AddSeconds(1)),
            string.Empty));
        var continuation = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray.Create(claim), null, null, string.Empty));
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, continuation).IsValid);
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        Assert.Equal(run.SequentialAdapterBinding?.GraphArtifactHash, context.Artifact.ArtifactHash);
        return new ApprovedCandidateFixture(run, claim, new HumanReviewContinuationCandidate(run, context.Artifact, continuation, claim), initial.EffectAttempt);
    }

    private static async Task<HumanReviewContinuationCandidate> DecisionCandidateAsync(HumanReviewDecisionKind kind)
    {
        var initial = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(initial.Run);
        var decisionAtUtc = initial.Run.UpdatedAtUtc.AddMinutes(1);
        var service = new HumanReviewDecisionService(store, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(decisionAtUtc));
        var detail = kind == HumanReviewDecisionKind.RequestInformation ? "Need a bounded clarification." : null;
        var expected = kind == HumanReviewDecisionKind.RequestInformation ? HumanReviewDecisionServiceStatus.InformationRequested : HumanReviewDecisionServiceStatus.Accepted;
        Assert.Equal(expected, (await service.DecideAsync(HumanReviewDecisionTestData.Command(initial.Run, "decision-" + kind.ToString().ToLowerInvariant(), kind, detail))).Status);
        var run = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        Assert.Equal(run.SequentialAdapterBinding?.GraphArtifactHash, context.Artifact.ArtifactHash);
        return new HumanReviewContinuationCandidate(run, context.Artifact, null, null);
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", correlationId, observedAtUtc, string.Empty));

    private static HumanReviewCurrentEffectAttemptEvidence EffectEvidence(HumanReviewBinding binding, GovernedLoopEffectAttempt effectAttempt)
        => new(HumanReviewEffectReleaseContract.CreateIdentity(binding, effectAttempt), HumanReviewEffectReleaseContract.CreatePreparation(binding, effectAttempt));

    private static HumanReviewCurrentEffectAttemptEvidenceReadResult CurrentEvidence(HumanReviewCurrentEffectAttemptEvidence evidence)
        => new(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, evidence);

    private static GovernedLoopEffectCertaintySnapshotResult CurrentSnapshot(HumanReviewEffectCertaintySnapshot snapshot)
        => new(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot);

    private static HumanReviewEffectCertaintySnapshot SnapshotFor(HumanReviewBinding binding, GovernedLoopEffectAttempt attempt, HumanReviewEffectCertainty certainty)
    {
        var current = certainty switch
        {
            HumanReviewEffectCertainty.Dispatched => Dispatched(attempt),
            HumanReviewEffectCertainty.Conclusive => Conclusive(attempt),
            HumanReviewEffectCertainty.Ambiguous => Ambiguous(attempt),
            HumanReviewEffectCertainty.Terminal => Terminal(attempt),
            _ => throw new ArgumentOutOfRangeException(nameof(certainty)),
        };
        return HumanReviewEffectReleaseContract.Create(binding, current, current.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Dispatched(GovernedLoopEffectAttempt attempt)
    {
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(attempt, Hash('a'), attempt.Payload.UpdatedAtUtc.AddSeconds(1));
        return GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, authorized.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Conclusive(GovernedLoopEffectAttempt attempt)
    {
        var dispatched = Dispatched(attempt);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-continuation-one", "after-continuation-one", dispatched.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Ambiguous(GovernedLoopEffectAttempt attempt)
    {
        var dispatched = Dispatched(attempt);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, null, null, dispatched.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Terminal(GovernedLoopEffectAttempt attempt)
    {
        var conclusive = Conclusive(attempt);
        return GovernedLoopEffectAttemptContract.Advance(conclusive, GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-continuation-one", "after-continuation-one", conclusive.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static string Hash(char value) => new(value, 64);

    private sealed record ApprovedCandidateFixture(CustomLoopRunRecord Run, HumanReviewContinuationClaim Claim, HumanReviewContinuationCandidate Candidate, GovernedLoopEffectAttempt? EffectAttempt);
}
