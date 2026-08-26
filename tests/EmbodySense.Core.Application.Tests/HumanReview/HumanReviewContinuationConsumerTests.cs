using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

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
        Assert.Equal(fixture.Claim.ClaimHash, result.Completion?.Claim.ClaimHash);
        Assert.Null(result.Action?.EffectQuery);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
    }

    [Fact]
    public async Task Expired_approved_claim_requests_only_expired_retirement_without_authority_or_effect_callbacks()
    {
        var fixture = await ApprovedCandidateAsync();
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource();
        var effectCertainty = new RecordingEffectCertaintySource();
        var consumer = Consumer(authority, effectEvidence, effectCertainty, fixture.Claim.LeaseExpiresAtUtc.AddTicks(1));

        var result = await consumer.ConsumeAsync(fixture.Candidate);

        Assert.Equal(HumanReviewContinuationConsumptionStatus.RetirementRequired, result.Status);
        Assert.Equal(HumanReviewContinuationOutcome.Expired, result.Retirement?.Outcome);
        Assert.Equal(HumanReviewContinuationRetirementReason.Expired, result.Retirement?.Reason);
        Assert.Null(result.Action);
        Assert.Null(result.Completion);
        Assert.Equal(0, authority.ReadCount);
        Assert.Equal(0, effectEvidence.ReadCount);
        Assert.Equal(0, effectCertainty.ReadCount);
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
    public async Task Exact_approved_effect_claim_prepares_only_an_exact_not_started_effect_release_and_completion_precondition()
    {
        var fixture = await ApprovedCandidateAsync(includeEffectAttempt: true);
        var binding = Assert.IsType<EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState>(fixture.Run.HumanReview).Request.Binding;
        var effectAttempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var evidence = EffectEvidence(binding, effectAttempt);
        var snapshot = HumanReviewEffectReleaseContract.Create(binding, effectAttempt, effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(
            CurrentEvidence(evidence),
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
        Assert.Equal(fixture.Claim.ClaimHash, result.Completion?.Claim.ClaimHash);
        Assert.Null(result.Retirement);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(3, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
        Assert.All(effectEvidence.Queries, query =>
        {
            Assert.Equal(binding, query.Binding);
            Assert.Equal(binding.EffectAttempt, query.EffectAttempt);
        });
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

    private static HumanReviewContinuationConsumer Consumer(
        RecordingAuthoritySource authority,
        RecordingEffectEvidenceSource effectEvidence,
        RecordingEffectCertaintySource effectCertainty,
        DateTimeOffset now)
        => new(authority, effectEvidence, effectCertainty, new HumanReviewDecisionTestClock(now));

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
