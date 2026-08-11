using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionServiceConsistencyTests
{
    [Fact]
    public async Task Three_pure_generation_conflicts_exhaust_as_ambiguous_after_exactly_three_commits()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        for (var generation = 1; generation <= 4; generation++)
        {
            harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
                GovernedLoopAdmissionStoreReadStatus.NotFound,
                generation,
                null));
        }

        for (var generation = 2; generation <= 4; generation++)
        {
            harness.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
                GovernedLoopAdmissionStoreCommitStatus.GenerationConflict,
                generation,
                null));
        }

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(3, harness.CommitCount);
        Assert.Equal(1, harness.FenceExecutionCount);
    }

    [Fact]
    public async Task Generation_conflict_followed_by_a_regressed_not_found_generation_is_ambiguous_without_retry()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            5,
            null));
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            4,
            null));
        harness.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.GenerationConflict,
            6,
            null));

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(1, harness.CommitCount);
        Assert.Equal(2, harness.StoreReadCount);
    }

    [Fact]
    public async Task Mixed_conflict_and_uncertain_recovery_never_resets_the_three_commit_budget()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResultFactory = readCount => readCount switch
        {
            1 => new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 1, null),
            2 => new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 2, null),
            3 => new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 3, null),
            _ => new GovernedLoopAdmissionStoreReadResult(
                GovernedLoopAdmissionStoreReadStatus.Recoverable,
                4,
                Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(harness.LastMutation?.Outcome)),
        };
        harness.CommitResultFactory = mutation => harness.CommitCount switch
        {
            1 => new GovernedLoopAdmissionStoreCommitResult(GovernedLoopAdmissionStoreCommitStatus.GenerationConflict, 2, null),
            2 => new GovernedLoopAdmissionStoreCommitResult(GovernedLoopAdmissionStoreCommitStatus.GenerationConflict, 3, null),
            _ => throw new InvalidOperationException("Injected uncertain third commit."),
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(3, harness.CommitCount);
        Assert.Equal(4, harness.StoreReadCount);
    }

    [Fact]
    public async Task Uncertain_commit_adopts_and_finalizes_a_different_exact_concurrent_winner()
    {
        var winnerSource = GovernedLoopAdmissionTestHarness.Create();
        _ = winnerSource.CreateRunId();
        var winner = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(
            (await winnerSource.CreateService().AdmitAsync(winnerSource.Request)).Outcome);
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            1,
            null));
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.Recoverable,
            2,
            winner));
        harness.CommitExceptions.Enqueue(new InvalidOperationException("Injected uncertain first commit."));

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
        Assert.Same(winner, result.Outcome);
        Assert.Equal(2, harness.CommitCount);
        Assert.Same(winner, harness.LastMutation?.Outcome);
    }

    [Theory]
    [InlineData(GovernedLoopAdmissionStoreReadStatus.Found)]
    [InlineData(GovernedLoopAdmissionStoreReadStatus.Recoverable)]
    public async Task Uncertain_new_commit_rejects_non_successor_terminal_generations(
        GovernedLoopAdmissionStoreReadStatus readStatus)
    {
        var winnerSource = GovernedLoopAdmissionTestHarness.Create();
        var winner = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(
            (await winnerSource.CreateService().AdmitAsync(winnerSource.Request)).Outcome);
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            5,
            null));
        harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(readStatus, 5, winner));
        harness.CommitExceptions.Enqueue(new InvalidOperationException("Injected uncertain commit."));

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(1, harness.CommitCount);
    }

    [Fact]
    public async Task Operation_conflict_with_impossible_generation_or_same_request_outcome_is_ambiguous()
    {
        var zeroGeneration = GovernedLoopAdmissionTestHarness.Create();
        zeroGeneration.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.OperationConflict,
            0,
            null));

        var zeroResult = await zeroGeneration.CreateService().AdmitAsync(zeroGeneration.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, zeroResult.Status);
        Assert.Null(zeroResult.Outcome);

        var missingProof = GovernedLoopAdmissionTestHarness.Create();
        missingProof.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.OperationConflict,
            2,
            null));

        var missingProofResult = await missingProof.CreateService().AdmitAsync(missingProof.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, missingProofResult.Status);
        Assert.Null(missingProofResult.Outcome);

        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var sameRequest = GovernedLoopAdmissionTestHarness.Create();
        sameRequest.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.OperationConflict,
            2,
            admitted));

        var sameRequestResult = await sameRequest.CreateService().AdmitAsync(sameRequest.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, sameRequestResult.Status);
        Assert.Null(sameRequestResult.Outcome);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("graph")]
    [InlineData("binding")]
    [InlineData("role")]
    [InlineData("grant")]
    [InlineData("capability")]
    public async Task Caller_cancellation_after_a_cooperative_or_ignoring_predurable_port_is_observed(string boundary)
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: boundary == "capability");
        using var cancellation = new CancellationTokenSource();
        harness.AfterStoreRead = _ =>
        {
            if (boundary == "store")
            {
                cancellation.Cancel();
            }
        };
        harness.AfterMutableRead = observed =>
        {
            if (observed == boundary)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreateService().AdmitAsync(harness.Request, cancellation.Token));

        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Active_grant_recorded_after_its_evaluation_is_ambiguous_before_effects()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var request = ReplaceGrant(
            harness,
            recordedAtUtc: harness.GrantResolution.EvaluatedAtUtc.AddTicks(1));

        await AssertTemporalGrantIsAmbiguousAsync(harness, request);
    }

    [Fact]
    public async Task Active_grant_effective_after_its_evaluation_is_ambiguous_before_effects()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var request = ReplaceGrant(
            harness,
            effectiveAtUtc: harness.GrantResolution.EvaluatedAtUtc.AddTicks(1));

        await AssertTemporalGrantIsAmbiguousAsync(harness, request);
    }

    [Fact]
    public async Task Active_grant_expired_at_final_trusted_time_is_ambiguous_before_effects()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var request = ReplaceGrant(
            harness,
            expiresAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(1));

        await AssertTemporalGrantIsAmbiguousAsync(harness, request);
    }

    [Fact]
    public async Task Artifact_created_after_grant_evaluation_is_ambiguous_before_effects()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var source = harness.Artifact.RevisionArtifact;
        var revisionArtifact = GovernedLoopRevisionArtifactFactory.Create(
            source.SchemaVersion,
            source.Revision,
            source.PredecessorRevision,
            source.RollbackSourcePublication,
            source.CreationOperationId,
            source.CreatedByActorId,
            harness.GrantResolution.EvaluatedAtUtc.AddTicks(1));
        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(
            harness.Artifact.SchemaVersion,
            revisionArtifact,
            harness.Artifact.Graph);
        harness.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(
            GovernedLoopRevisionStoreReadStatus.Ready,
            1,
            artifact);
        harness.BindingResolution = harness.BindingResolution with { Artifact = artifact };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Role_provenance_recorded_after_lifecycle_evidence_is_ambiguous_before_effects()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(
            roleRecordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-5));
        var lifecycle = Assert.IsType<EmbodySense.Core.Application.ContextualRoles.Models.ContextualRoleLifecycleSnapshot>(
            harness.RoleResolution.Lifecycle);
        Assert.True(harness.Role.Provenance.RecordedAtUtc > lifecycle.UpdatedAtUtc);
        Assert.True(harness.Role.Provenance.RecordedAtUtc <= harness.GrantResolution.EvaluatedAtUtc);

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
        Assert.Equal(0, harness.CommitCount);
    }

    private static GovernedLoopAdmissionRequest ReplaceGrant(
        GovernedLoopAdmissionTestHarness harness,
        DateTimeOffset? recordedAtUtc = null,
        DateTimeOffset? effectiveAtUtc = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        var grant = AuthorityGrantHash.Apply(harness.Grant with
        {
            RecordedAtUtc = recordedAtUtc ?? harness.Grant.RecordedAtUtc,
            Boundary = harness.Grant.Boundary with
            {
                EffectiveAtUtc = effectiveAtUtc ?? harness.Grant.Boundary.EffectiveAtUtc,
                ExpiresAtUtc = expiresAtUtc ?? harness.Grant.Boundary.ExpiresAtUtc,
            },
            ContentHash = string.Empty,
        });
        var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        harness.GrantResolution = harness.GrantResolution with
        {
            RequestedReference = reference,
            Grant = grant,
        };
        return GovernedLoopAdmissionRequestHash.Apply(harness.Request with { AuthorityGrant = reference });
    }

    private static async Task AssertTemporalGrantIsAmbiguousAsync(
        GovernedLoopAdmissionTestHarness harness,
        GovernedLoopAdmissionRequest request)
    {
        var result = await harness.CreateService().AdmitAsync(request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
        Assert.Equal(0, harness.CommitCount);
    }
}
