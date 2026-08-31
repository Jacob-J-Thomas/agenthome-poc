using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

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
        Assert.Equal(fixture.Request.Binding.WorkspaceId, readStore.LastWorkspaceId);

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
        Assert.Equal(fixture.Request.Binding.WorkspaceId, readStore.LastWorkspaceId);
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
    public async Task Root_rejected_effect_attempt_reads_fail_closed_after_forwarding_the_exact_review_workspace()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt))
        {
            RequiredWorkspaceId = DifferentWorkspaceId(fixture.Request.Binding.WorkspaceId),
        };
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(fixture.Request.Binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(fixture.Request.Binding, attempt);

        var evidence = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));
        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable, evidence.Status);
        Assert.Equal(fixture.Request.Binding.WorkspaceId, readStore.LastWorkspaceId);

        var certainty = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));
        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Unavailable, certainty.Status);
        Assert.Equal(fixture.Request.Binding.WorkspaceId, readStore.LastWorkspaceId);
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

    [Fact]
    public async Task Released_read_reconstructs_the_canonical_missing_post_release_state_from_a_valid_expectation()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var release = SyntheticRelease(fixture.Run, fixture.Request, attempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);
        var query = new HumanReviewPreDispatchEffectReleaseEvidenceQuery(
            fixture.Request.Binding.WorkspaceId,
            attempt.AdmissionAuthorityEvidenceHash,
            release,
            attempt);

        var status = await source.ReadReleasedAsync(query);

        Assert.Equal(HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Missing, status);
        Assert.Equal(1, readStore.ReadCount);
        Assert.Equal(fixture.Request.Binding.WorkspaceId, readStore.LastWorkspaceId);
    }

    [Fact]
    public async Task Released_read_rejects_an_invalid_expectation_before_touching_the_canonical_stores()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, fixture.EffectAttempt));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);

        var status = await source.ReadReleasedAsync(new HumanReviewPreDispatchEffectReleaseEvidenceQuery(
            fixture.Request.Binding.WorkspaceId,
            attempt.AdmissionAuthorityEvidenceHash,
            null!,
            null!));

        Assert.Equal(HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Corrupt, status);
        Assert.Equal(0, readStore.ReadCount);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAttemptReadStatus.Missing, HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Missing)]
    [InlineData(GovernedLoopEffectAttemptReadStatus.Corrupt, HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Corrupt)]
    [InlineData(GovernedLoopEffectAttemptReadStatus.Unavailable, HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Unavailable)]
    public async Task Released_read_maps_closed_canonical_attempt_postures(
        GovernedLoopEffectAttemptReadStatus sourceStatus,
        HumanReviewPreDispatchEffectReleaseEvidenceReadStatus expectedStatus)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var release = SyntheticRelease(fixture.Run, fixture.Request, attempt);
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(sourceStatus));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);

        var status = await source.ReadReleasedAsync(new HumanReviewPreDispatchEffectReleaseEvidenceQuery(
            fixture.Request.Binding.WorkspaceId,
            attempt.AdmissionAuthorityEvidenceHash,
            release,
            attempt));

        Assert.Equal(expectedStatus, status);
        Assert.Equal(1, readStore.ReadCount);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("admission")]
    [InlineData("content")]
    public async Task Released_read_rejects_scope_and_content_expectation_drift_before_classification(string drift)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var release = SyntheticRelease(fixture.Run, fixture.Request, attempt);
        var canonicalHead = drift == "content" ? Dispatched(attempt) : attempt;
        var readStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, canonicalHead));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), readStore);
        var workspaceId = drift == "workspace" ? DifferentWorkspaceId(fixture.Request.Binding.WorkspaceId) : fixture.Request.Binding.WorkspaceId;
        var admissionHash = drift == "admission" ? Hash('a') : attempt.AdmissionAuthorityEvidenceHash;
        var expectedAttempt = attempt;

        var status = await source.ReadReleasedAsync(new HumanReviewPreDispatchEffectReleaseEvidenceQuery(workspaceId, admissionHash, release, expectedAttempt));

        Assert.Equal(HumanReviewPreDispatchEffectReleaseEvidenceReadStatus.Stale, status);
        Assert.Equal(drift == "content" ? 1 : 0, readStore.ReadCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canonical_current_evidence_maps_run_source_failures_to_closed_statuses(bool formatFailure)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var runStore = new HumanReviewDecisionTestStore(fixture.Run)
        {
            GetOverrideAsync = (_, _) => throw (formatFailure ? new FormatException("corrupt run") : new IOException("run unavailable")),
        };
        var source = new CanonicalHumanReviewEffectEvidenceSource(runStore, new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt)));
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);

        var result = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));

        Assert.Equal(formatFailure ? HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt : HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Canonical_current_evidence_maps_missing_run_and_invalid_attempt_heads_to_closed_statuses()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var missingRun = new HumanReviewDecisionTestStore(fixture.Run)
        {
            GetOverrideAsync = (_, _) => Task.FromResult<CustomLoopRunRecord?>(null),
        };
        var missingSource = new CanonicalHumanReviewEffectEvidenceSource(missingRun, new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, attempt)));
        var missing = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)missingSource).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));

        var invalidAttemptStore = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult((GovernedLoopEffectAttemptReadStatus)999));
        var invalidAttemptSource = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), invalidAttemptStore);
        var invalidAttempt = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)invalidAttemptSource).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Missing, missing.Status);
        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt, invalidAttempt.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canonical_current_evidence_maps_effect_source_failures_to_unavailable(bool formatFailure)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var attempts = new ThrowingHumanReviewEffectAttemptReadStore(formatFailure ? new FormatException("corrupt effect") : new IOException("effect unavailable"));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), attempts);

        var result = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable, result.Status);
        Assert.Equal(1, attempts.ReadCount);
    }

    [Fact]
    public async Task Canonical_current_evidence_propagates_cancellation_from_the_run_source()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, fixture.EffectAttempt)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(
            new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed), cancellation.Token));
    }

    [Fact]
    public async Task Canonical_current_evidence_propagates_cancellation_from_the_effect_source_after_the_run_read()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        using var cancellation = new CancellationTokenSource();
        var runStore = new HumanReviewDecisionTestStore(fixture.Run)
        {
            GetOverrideAsync = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<CustomLoopRunRecord?>(fixture.Run);
            },
        };
        var source = new CanonicalHumanReviewEffectEvidenceSource(
            runStore,
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, fixture.EffectAttempt)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(
            new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed), cancellation.Token));
    }

    [Fact]
    public async Task Canonical_current_evidence_and_certainty_reject_an_invalid_current_attempt_head()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(fixture.EffectAttempt);
        var reviewed = Assert.IsType<HumanReviewEffectAttemptBinding>(fixture.Request.Binding.EffectAttempt);
        var invalid = attempt with { ContentHash = Hash('a') };
        var source = new CanonicalHumanReviewEffectEvidenceSource(
            new HumanReviewDecisionTestStore(fixture.Run),
            new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, invalid)));
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(fixture.Request.Binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(fixture.Request.Binding, attempt);

        var evidence = await ((IHumanReviewCurrentEffectAttemptEvidenceSource)source).ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(fixture.Request.Binding, reviewed));
        var certainty = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));

        Assert.Equal(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt, evidence.Status);
        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Corrupt, certainty.Status);
    }

    [Fact]
    public async Task Canonical_certainty_rejects_a_missing_expectation_without_reading_the_stores()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(includeEffectAttempt: true);
        var attempts = new RecordingHumanReviewEffectAttemptReadStore(new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Current, fixture.EffectAttempt));
        var source = new CanonicalHumanReviewEffectEvidenceSource(new HumanReviewDecisionTestStore(fixture.Run), attempts);

        var result = await ((IGovernedLoopEffectCertaintySnapshotSource)source).ReadAsync(new GovernedLoopEffectCertaintySnapshotQuery(null!, null!));

        Assert.Equal(GovernedLoopEffectCertaintySnapshotStatus.Corrupt, result.Status);
        Assert.Equal(0, attempts.ReadCount);
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

    private static HumanReviewPreDispatchEffectRelease SyntheticRelease(CustomLoopRunRecord run, HumanReviewRequest request, GovernedLoopEffectAttempt attempt)
    {
        var generation = run.SequentialAdapterBinding!.ExecutionBinding.ExecutionGeneration;
        var requestReference = new HumanReviewRequestReference(request.RequestId, request.RequestHash);
        var wakeReference = new HumanReviewContinuationWakeReference("synthetic-wake", Hash('a'));
        var claimReference = new HumanReviewContinuationClaimReference("synthetic-claim", Hash('b'));
        var reservationReference = new HumanReviewContinuationReservationReference("synthetic-reservation", Hash('c'));
        var operationId = Assert.IsType<string>(HumanReviewContinuationReleaseOperationId.Create(
            requestReference,
            wakeReference,
            reservationReference,
            generation,
            HumanReviewContinuationReleaseKind.PreDispatchEffect));
        var effectReceiptHash = HumanReviewEffectReleaseContract.Create(request.Binding, attempt, attempt.Payload.UpdatedAtUtc).SnapshotHash;
        var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(
            HumanReviewContinuationReleaseReceipt.CurrentSchemaVersion,
            operationId,
            wakeReference,
            claimReference,
            reservationReference,
            generation,
            HumanReviewContinuationReleaseKind.PreDispatchEffect,
            HumanReviewContinuationReleaseDisposition.Released,
            Hash('d'),
            Hash('e'),
            effectReceiptHash,
            string.Empty));
        return new HumanReviewPreDispatchEffectRelease(request, receipt);
    }

    private static string DifferentWorkspaceId(string workspaceId)
        => workspaceId[..^1] + (workspaceId[^1] == 'a' ? "b" : "a");

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);
}
