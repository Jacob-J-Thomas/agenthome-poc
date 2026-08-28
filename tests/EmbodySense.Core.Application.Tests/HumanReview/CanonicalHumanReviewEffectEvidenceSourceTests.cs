using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class CanonicalHumanReviewEffectEvidenceSourceTests
{
    [Fact]
    public async Task Canonical_reads_derive_safe_current_evidence_and_dispatched_certainty_without_a_lease_or_payload_projection()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);
        var reviewed = Assert.IsType<EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);

        var evidence = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, evidence.Status);
        Assert.Equal(reviewed.EffectAttemptId, evidence.Evidence?.Identity.EffectId);
        Assert.Equal(reviewed.PreparationHash, evidence.Evidence?.Preparation.PreparationHash);
        Assert.Equal(1, readStore.ReadCount);

        var dispatched = GovernedLoopEffectAttemptContract.Advance(
            GovernedLoopEffectAttemptContract.AttachDispatchAuthority(attempt, Hash('9'), attempt.Payload.UpdatedAtUtc.AddSeconds(1)),
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            attempt.Payload.UpdatedAtUtc.AddSeconds(2));
        readStore.Result = new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, dispatched);

        var certainty = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(evidence.Evidence!.Identity, evidence.Evidence.Preparation));

        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Current, certainty.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, certainty.Snapshot?.Phase);
        Assert.NotNull(certainty.Snapshot?.SnapshotHash);
        Assert.Equal(2, readStore.ReadCount);
    }

    [Fact]
    public async Task Corrupt_or_mismatched_canonical_effect_evidence_fails_closed()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var reviewed = Assert.IsType<EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var corruptStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Corrupt));
        var corrupt = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), corruptStore);

        var corrupted = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)corrupt).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));
        var stale = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, fixture.EffectAttempt))))
            .ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding with { BindingHash = Hash('8') }, reviewed));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt, corrupted.Status);
        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt, stale.Status);
    }

    [Fact]
    public async Task Self_consistent_effect_attempt_from_a_different_execution_generation_fails_closed()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true, effectAttemptExecutionGenerationOffset: 1);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var source = new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt)));

        var evidence = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(fixture.Request.Binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(fixture.Request.Binding, attempt);
        var certainty = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale, evidence.Status);
        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Corrupt, certainty.Status);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAttemptReadStatus.Missing, HumanReviewCurrentEffectAttemptEvidenceReadStatus.Missing, GovernedLoopEffectCertaintySnapshotStatus.Missing)]
    [InlineData(GovernedLoopEffectAttemptReadStatus.Unavailable, HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable, GovernedLoopEffectCertaintySnapshotStatus.Unavailable)]
    public async Task Missing_and_unavailable_effect_attempt_reads_map_to_closed_read_only_postures(
        GovernedLoopEffectAttemptReadStatus sourceStatus,
        HumanReviewCurrentEffectAttemptEvidenceReadStatus expectedEvidenceStatus,
        GovernedLoopEffectCertaintySnapshotStatus expectedCertaintyStatus)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(sourceStatus));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(fixture.Request.Binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(fixture.Request.Binding, attempt);

        var evidence = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));
        var certainty = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));

        Assert.Equal(expectedEvidenceStatus, evidence.Status);
        Assert.Equal(expectedCertaintyStatus, certainty.Status);
        Assert.Equal(2, readStore.ReadCount);
    }

    [Fact]
    public async Task Canonical_read_distinguishes_conclusive_and_ambiguous_effect_certainty()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(fixture.Request.Binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(fixture.Request.Binding, attempt);
        var conclusive = new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, Conclusive(attempt))));
        var ambiguous = new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, Ambiguous(attempt))));

        var conclusiveResult = await ((IGovernedLoopEffectCertaintySnapshotSource)conclusive).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));
        var ambiguousResult = await ((IGovernedLoopEffectCertaintySnapshotSource)ambiguous).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));

        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Current, conclusiveResult.Status);
        Assert.Equal(HumanReviewEffectCertainty.Conclusive, conclusiveResult.Snapshot?.Certainty);
        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Current, ambiguousResult.Status);
        Assert.Equal(HumanReviewEffectCertainty.Ambiguous, ambiguousResult.Snapshot?.Certainty);
    }

    private static GovernedLoopEffectAttempt Conclusive(GovernedLoopEffectAttempt attempt)
    {
        var dispatched = Dispatched(attempt);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-evidence-one", "after-evidence-one", dispatched.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Ambiguous(GovernedLoopEffectAttempt attempt)
    {
        var dispatched = Dispatched(attempt);
        return GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, null, null, dispatched.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static GovernedLoopEffectAttempt Dispatched(GovernedLoopEffectAttempt attempt)
    {
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(attempt, Hash('a'), attempt.Payload.UpdatedAtUtc.AddSeconds(1));
        return GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, authorized.Payload.UpdatedAtUtc.AddSeconds(1));
    }

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);
}
