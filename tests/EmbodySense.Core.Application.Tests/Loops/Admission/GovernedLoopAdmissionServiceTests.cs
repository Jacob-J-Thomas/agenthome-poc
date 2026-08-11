using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionServiceTests
{
    [Fact]
    public void Generated_run_id_is_unique_and_accepted_by_the_execution_binding_contract()
    {
        var generator = new GovernedLoopAdmissionRunIdentityGenerator();
        var revision = AuthorityGrantApplicationTestFixture.LoopPin().Revision;

        var first = GovernedLoopExecutionBinding.Create(1, generator.CreateRunId(), revision, 1);
        var second = GovernedLoopExecutionBinding.Create(1, generator.CreateRunId(), revision, 1);

        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task Empty_authority_admission_commits_exact_generation_one_receipt_inside_one_fence()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Admitted, result.Status);
        Assert.Equal(harness.Request.OperationId, result.OperationId);
        Assert.Equal(harness.Request.RequestHash, result.RequestHash);
        var outcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(result.Outcome);
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        Assert.Equal(GovernedLoopAdmissionDisposition.Admitted, outcome.Disposition);
        var receipt = Assert.IsType<GovernedLoopAdmissionReceipt>(outcome.Receipt);
        Assert.Null(outcome.Rejection);
        Assert.Equal(1, receipt.Evidence.Binding.ExecutionGeneration);
        Assert.Equal(harness.Publication.Revision, receipt.Evidence.Binding.Revision);
        Assert.Equal(harness.Grant.Binding.Profile, receipt.Evidence.GrantProfile);
        Assert.Equal(harness.Grant.Boundary, receipt.Evidence.GrantBoundary);
        Assert.Equal(harness.GrantResolution.DependencyEvidenceHash, receipt.Evidence.GrantDependencyEvidenceHash);
        Assert.Empty(receipt.Evidence.EffectiveAuthority.Capabilities);
        Assert.Empty(receipt.Evidence.CapabilityAdmission.Requirements.Required);
        Assert.Empty(receipt.Evidence.CapabilityAdmission.Requirements.Optional);
        Assert.Empty(receipt.Evidence.CapabilityAdmission.Pins);
        Assert.Empty(receipt.Evidence.CapabilityAdmission.Evidence);
        Assert.Equal(7, receipt.Evidence.References.Count);
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(1, harness.RunIdentityGenerationCount);
        Assert.Equal(1, harness.FenceExecutionCount);
        Assert.Equal(1, harness.CommitCount);
        Assert.True(harness.CommitObservedInsideFence);
        Assert.Same(outcome, harness.LastMutation?.Outcome);
        Assert.Equal(outcome.Intent.WorkspaceId, harness.LastMutation?.WorkspaceId);
        Assert.Equal(GovernedLoopAdmissionContractHash.ComputeIntentHash(outcome.Intent), harness.LastMutation?.IntentHash);
    }

    [Fact]
    public async Task Capability_admission_receipt_retains_the_exact_non_widened_graph_and_grant_intersection()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        harness.CapabilityResultFactory = (requirements, allowed) =>
            new CapabilityAdmissionResult(true, CreateCapabilitySnapshot(requirements, Assert.Single(allowed)), "Admitted.");

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Admitted, result.Status);
        var evidence = Assert.IsType<GovernedLoopAdmissionReceipt>(result.Outcome?.Receipt).Evidence;
        var allowed = Assert.Single(harness.LastAllowedCapabilityIds!);
        Assert.Equal(Assert.Single(harness.Artifact.Graph.AuthorityCeiling.CapabilityIds), allowed.Value);
        Assert.Equal(Assert.Single(harness.EffectiveCeiling.Capabilities).Id, allowed);
        Assert.Equal(Assert.Single(evidence.EffectiveAuthority.Capabilities).Id, allowed);
        Assert.Equal(allowed, Assert.Single(evidence.CapabilityAdmission.Pins).DescriptorIdentity.Id);
        Assert.Equal(allowed, Assert.Single(evidence.CapabilityAdmission.Requirements.Required).CapabilityId);
        Assert.True(GovernedLoopAdmissionValidator.Validate(result.Outcome).IsValid);
    }

    [Fact]
    public async Task Exact_admitted_replay_precedes_mutable_sources_and_run_identity_generation()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await harness.CreateService().AdmitAsync(harness.Request)).Outcome);
        var replay = GovernedLoopAdmissionTestHarness.Create();
        replay.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 19, admitted);
        replay.ThrowMutableReads = true;
        replay.ThrowRunIdentityGeneration = true;

        var result = await replay.CreateService().AdmitAsync(replay.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
        Assert.Same(admitted, result.Outcome);
        AssertNoMutableWork(replay);
    }

    [Fact]
    public async Task Exact_rejected_history_replays_without_reinterpreting_current_sources()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var roleReference = new GovernedLoopAdmissionEvidenceReference(
            GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
            GovernedLoopAdmissionContractHash.ComputeContextualRoleReferenceHash(admitted.Intent.Role));
        var rejection = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionRejection(
            GovernedLoopAdmissionRejection.CurrentSchemaVersion,
            admitted.Intent,
            GovernedLoopAdmissionFailureCode.RoleInactive,
            null,
            null,
            [roleReference],
            admitted.RecordedAtUtc,
            string.Empty));
        var rejected = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            admitted.Intent,
            GovernedLoopAdmissionDisposition.Rejected,
            null,
            rejection,
            admitted.RecordedAtUtc,
            string.Empty));
        var replay = GovernedLoopAdmissionTestHarness.Create();
        replay.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 20, rejected);
        replay.ThrowMutableReads = true;
        replay.ThrowRunIdentityGeneration = true;

        var result = await replay.CreateService().AdmitAsync(replay.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
        Assert.Same(rejected, result.Outcome);
        Assert.Equal(GovernedLoopAdmissionDisposition.Rejected, result.Outcome?.Disposition);
        AssertNoMutableWork(replay);
    }

    [Fact]
    public async Task Changed_caller_stable_replay_coordinates_conflict_before_mutable_reads()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var alternatePublication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            seed.Publication.Revision,
            "publish-loop-other",
            AuthorityGrantApplicationTestFixture.Hash64('9'));
        var alternateGrant = new AuthorityGrantReference(seed.GrantReference.GrantId, seed.GrantReference.Revision, "sha256:" + AuthorityGrantApplicationTestFixture.Hash64('9'));
        var changedRequests = new[]
        {
            GovernedLoopAdmissionRequestHash.Apply(seed.Request with { InvocationPayloadHash = AuthorityGrantApplicationTestFixture.Hash64('9') }),
            GovernedLoopAdmissionRequestHash.Apply(seed.Request with { Publication = alternatePublication }),
            GovernedLoopAdmissionRequestHash.Apply(seed.Request with { AuthorityGrant = alternateGrant }),
            GovernedLoopAdmissionRequestHash.Apply(seed.Request with { ActorId = AuthorityGrantApplicationTestFixture.Actor("user-other") }),
            GovernedLoopAdmissionRequestHash.Apply(seed.Request with { Surface = "cli" }),
        };

        foreach (var changed in changedRequests)
        {
            var replay = GovernedLoopAdmissionTestHarness.Create();
            replay.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 3, admitted);
            replay.ThrowMutableReads = true;
            replay.ThrowRunIdentityGeneration = true;

            var result = await replay.CreateService().AdmitAsync(changed);

            Assert.Equal(GovernedLoopAdmissionStatus.Conflict, result.Status);
            Assert.Null(result.Outcome);
            AssertNoMutableWork(replay);
        }
    }

    [Fact]
    public async Task Invalid_request_never_reads_the_store_or_enters_the_authority_fence()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var invalid = GovernedLoopAdmissionRequestHash.Apply(harness.Request with { SchemaVersion = 2 });
        var forged = harness.Request with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('9') };
        var overlongOperation = harness.Request with
        {
            OperationId = new string('a', GovernedLoopAdmissionLimits.MaxIdentifierCharacters + 1),
        };
        var overlongSurface = harness.Request with
        {
            Surface = new string('a', GovernedLoopAdmissionLimits.MaxSurfaceCharacters + 1),
        };
        var malformedPublication = harness.Request with
        {
            Publication = harness.Publication with { PublicationOperationId = new string('a', 10_000) },
        };

        var result = await harness.CreateService().AdmitAsync(invalid);
        var forgedResult = await harness.CreateService().AdmitAsync(forged);
        var overlongOperationResult = await harness.CreateService().AdmitAsync(overlongOperation);
        var overlongSurfaceResult = await harness.CreateService().AdmitAsync(overlongSurface);
        var malformedPublicationResult = await harness.CreateService().AdmitAsync(malformedPublication);
        var absent = await harness.CreateService().AdmitAsync(null);

        Assert.All(
            new[] { result, forgedResult, overlongOperationResult, overlongSurfaceResult, malformedPublicationResult, absent },
            item =>
            {
                Assert.Equal(GovernedLoopAdmissionStatus.Invalid, item.Status);
                Assert.Empty(item.RequestHash);
                Assert.Null(item.Outcome);
            });
        Assert.Empty(overlongOperationResult.OperationId);
        Assert.Equal(harness.Request.OperationId, overlongSurfaceResult.OperationId);
        Assert.Equal(harness.Request.OperationId, malformedPublicationResult.OperationId);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.StoreReadCount);
        Assert.Equal(0, harness.FenceExecutionCount);
    }

    [Fact]
    public async Task Corrupt_or_foreign_stored_outcome_is_ambiguous()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var foreign = RebindWorkspace(admitted, "workspace-sha256:" + AuthorityGrantApplicationTestFixture.Hash64('b'));
        var storedOutcomes = new[]
        {
            admitted with { ContentHash = AuthorityGrantApplicationTestFixture.Hash64('f') },
            foreign,
        };

        foreach (var stored in storedOutcomes)
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            harness.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 2, stored);

            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(1, harness.FenceExecutionCount);
        }
    }

    [Fact]
    public async Task Exact_recoverable_outcome_is_finalized_at_the_successor_generation_and_replayed()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);

        foreach (var commitStatus in new[]
                 {
                     GovernedLoopAdmissionStoreCommitStatus.Committed,
                     GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted,
                 })
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            harness.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Recoverable, 8, admitted);
            harness.ThrowMutableReads = true;
            harness.ThrowRunIdentityGeneration = true;
            harness.CommitResultFactory = mutation => new GovernedLoopAdmissionStoreCommitResult(commitStatus, 9, mutation.Outcome);

            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
            Assert.Same(admitted, result.Outcome);
            Assert.Equal(1, harness.CommitCount);
            Assert.Equal(8, harness.LastMutation?.ExpectedStoreGeneration);
            Assert.Same(admitted, harness.LastMutation?.Outcome);
            Assert.True(harness.CommitObservedInsideFence);
            Assert.Equal(0, harness.GraphReadCount);
            Assert.Equal(0, harness.BindingReadCount);
            Assert.Equal(0, harness.RoleReadCount);
            Assert.Equal(0, harness.GrantReadCount);
            Assert.Equal(0, harness.CapabilityAdmissionCount);
            Assert.Equal(0, harness.RunIdentityGenerationCount);
        }
    }

    [Fact]
    public async Task Recoverable_history_conflict_or_corruption_never_commits()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var changedRequest = GovernedLoopAdmissionRequestHash.Apply(seed.Request with
        {
            InvocationPayloadHash = AuthorityGrantApplicationTestFixture.Hash64('9'),
        });
        var conflict = GovernedLoopAdmissionTestHarness.Create();
        conflict.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Recoverable, 8, admitted);

        var conflictResult = await conflict.CreateService().AdmitAsync(changedRequest);

        Assert.Equal(GovernedLoopAdmissionStatus.Conflict, conflictResult.Status);
        Assert.Null(conflictResult.Outcome);
        Assert.Equal(0, conflict.CommitCount);

        foreach (var stored in new[]
                 {
                     admitted with { ContentHash = AuthorityGrantApplicationTestFixture.Hash64('f') },
                     RebindWorkspace(admitted, "workspace-sha256:" + AuthorityGrantApplicationTestFixture.Hash64('b')),
                 })
        {
            var malformed = GovernedLoopAdmissionTestHarness.Create();
            malformed.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Recoverable, 8, stored);

            var result = await malformed.CreateService().AdmitAsync(malformed.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(0, malformed.CommitCount);
        }
    }

    [Fact]
    public async Task Nonterminal_capability_denial_is_unavailable_and_never_commits()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        harness.CapabilityResult = new CapabilityAdmissionResult(false, null, "Catalog temporarily unavailable.");

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Unavailable, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(1, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Malformed_admitted_capability_snapshot_is_ambiguous_and_never_commits()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        harness.CapabilityResultFactory = (requirements, allowed) =>
        {
            var coherent = CreateCapabilitySnapshot(requirements, Assert.Single(allowed));
            return new CapabilityAdmissionResult(true, coherent with { RequirementsHash = AuthorityGrantApplicationTestFixture.Hash64('f') }, "Admitted.");
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Active_dependency_shape_mismatches_are_ambiguous()
    {
        var mutations = new Action<GovernedLoopAdmissionTestHarness>[]
        {
            harness => harness.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                1,
                AuthorityGrantApplicationTestFixture.GraphArtifact(harness.RolePin, ["org.embodysense/workspace/write"])),
            harness => harness.BindingResolution = harness.BindingResolution with
            {
                PublicationPin = GovernedLoopRevisionPublicationPinFactory.Create(
                    1,
                    harness.Publication.Revision,
                    "publish-loop-other",
                    AuthorityGrantApplicationTestFixture.Hash64('9')),
            },
            harness => harness.RoleResolution = harness.RoleResolution with
            {
                RequestedPin = new ContextualRoleRevisionPin(
                    AuthorityGrantApplicationTestFixture.Role(roleId: "other-role").Identity,
                    AuthorityGrantApplicationTestFixture.Role(roleId: "other-role").ContentHash),
            },
            harness => harness.GrantResolution = harness.GrantResolution with
            {
                RequestedReference = new AuthorityGrantReference(
                    harness.GrantReference.GrantId,
                    harness.GrantReference.Revision,
                    "sha256:" + AuthorityGrantApplicationTestFixture.Hash64('9')),
            },
        };

        foreach (var mutate in mutations)
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            mutate(harness);

            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(0, harness.CommitCount);
        }
    }

    [Fact]
    public async Task Exact_already_committed_result_replays_proposed_outcome()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.CommitResultFactory = mutation => new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted,
            mutation.ExpectedStoreGeneration + 1,
            mutation.Outcome);

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
        Assert.NotNull(result.Outcome);
        Assert.Equal(1, harness.CommitCount);
        Assert.True(harness.CommitObservedInsideFence);
    }

    [Fact]
    public async Task Repeated_optimistic_generation_conflicts_are_bounded_and_do_not_publish_an_outcome()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        for (var generation = 1; generation <= 8; generation++)
        {
            harness.StoreReadResults.Enqueue(new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, generation, null));
            harness.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(GovernedLoopAdmissionStoreCommitStatus.GenerationConflict, generation + 1, null));
        }

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(3, harness.CommitCount);
        Assert.Equal(1, harness.FenceExecutionCount);
    }

    [Fact]
    public async Task Store_retention_limit_is_distinct_and_has_no_terminal_outcome()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.CommitResults.Enqueue(new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.LimitExceeded,
            1,
            null));

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.LimitExceeded, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(1, harness.CommitCount);
    }

    [Fact]
    public async Task Recoverable_outcome_finalization_ignores_caller_cancellation_after_the_durable_read()
    {
        var seed = GovernedLoopAdmissionTestHarness.Create();
        var admitted = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await seed.CreateService().AdmitAsync(seed.Request)).Outcome);
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Recoverable, 8, admitted);
        using var cancellation = new CancellationTokenSource();
        harness.AfterStoreRead = _ => cancellation.Cancel();

        var result = await harness.CreateService().AdmitAsync(harness.Request, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, result.Status);
        Assert.Same(admitted, result.Outcome);
        Assert.Equal(1, harness.CommitCount);
        Assert.True(harness.CommitObservedInsideFence);
    }

    [Fact]
    public async Task Repeated_failed_recoverable_finalization_is_bounded_and_ambiguous()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.StoreReadResultFactory = readCount => readCount == 1
            ? new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 1, null)
            : new GovernedLoopAdmissionStoreReadResult(
                GovernedLoopAdmissionStoreReadStatus.Recoverable,
                readCount,
                Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(harness.LastMutation?.Outcome));
        for (var attempt = 0; attempt < 16; attempt++)
        {
            harness.CommitExceptions.Enqueue(new InvalidOperationException("Injected durable-store failure."));
        }

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.InRange(harness.CommitCount, 2, 3);
        Assert.InRange(harness.StoreReadCount, 2, 4);
        Assert.Equal(1, harness.FenceExecutionCount);
    }

    [Fact]
    public async Task Impossible_commit_generations_are_ambiguous()
    {
        var leap = GovernedLoopAdmissionTestHarness.Create();
        leap.CommitResultFactory = mutation => new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.Committed,
            mutation.ExpectedStoreGeneration + 2,
            mutation.Outcome);

        var leapResult = await leap.CreateService().AdmitAsync(leap.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, leapResult.Status);
        Assert.Null(leapResult.Outcome);

        var overflow = GovernedLoopAdmissionTestHarness.Create();
        overflow.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(
            GovernedLoopAdmissionStoreReadStatus.NotFound,
            long.MaxValue,
            null);

        var overflowResult = await overflow.CreateService().AdmitAsync(overflow.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, overflowResult.Status);
        Assert.Null(overflowResult.Outcome);

        var alreadyCommitted = GovernedLoopAdmissionTestHarness.Create();
        alreadyCommitted.CommitResultFactory = mutation => new GovernedLoopAdmissionStoreCommitResult(
            GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted,
            0,
            mutation.Outcome);

        var alreadyCommittedResult = await alreadyCommitted.CreateService().AdmitAsync(alreadyCommitted.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, alreadyCommittedResult.Status);
        Assert.Null(alreadyCommittedResult.Outcome);
    }

    [Fact]
    public async Task Active_role_with_malformed_lifecycle_evidence_is_ambiguous_and_never_commits()
    {
        var corruptions = new Func<ContextualRoleLifecycleSnapshot, ContextualRoleLifecycleSnapshot>[]
        {
            lifecycle => lifecycle with { LastOperationId = "" },
            lifecycle => lifecycle with { LastMutationKind = ContextualRoleRevisionMutationKind.Unknown },
            lifecycle => lifecycle with { UpdatedAtUtc = default },
            lifecycle => lifecycle with
            {
                UpdatedAtUtc = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.FromHours(1)),
            },
        };

        foreach (var corrupt in corruptions)
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            harness.RoleResolution = harness.RoleResolution with
            {
                Lifecycle = corrupt(Assert.IsType<ContextualRoleLifecycleSnapshot>(harness.RoleResolution.Lifecycle)),
            };

            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(0, harness.CommitCount);
        }
    }

    [Fact]
    public async Task Role_evidence_recorded_after_grant_evaluation_is_ambiguous_before_capability_or_commit()
    {
        var lifecycleFuture = GovernedLoopAdmissionTestHarness.Create();
        lifecycleFuture.RoleResolution = lifecycleFuture.RoleResolution with
        {
            Lifecycle = Assert.IsType<ContextualRoleLifecycleSnapshot>(lifecycleFuture.RoleResolution.Lifecycle) with
            {
                UpdatedAtUtc = lifecycleFuture.GrantResolution.EvaluatedAtUtc.AddTicks(1),
            },
        };
        var provenanceFuture = GovernedLoopAdmissionTestHarness.Create(
            roleRecordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddTicks(1));

        foreach (var harness in new[] { lifecycleFuture, provenanceFuture })
        {
            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(ReferenceEquals(harness, provenanceFuture) ? 0 : 1, harness.GrantReadCount);
            Assert.Equal(0, harness.CapabilityAdmissionCount);
            Assert.Equal(0, harness.RunIdentityGenerationCount);
            Assert.Equal(0, harness.CommitCount);
        }
    }

    private static void AssertNoMutableWork(GovernedLoopAdmissionTestHarness harness)
    {
        Assert.Equal(0, harness.GraphReadCount);
        Assert.Equal(0, harness.BindingReadCount);
        Assert.Equal(0, harness.RoleReadCount);
        Assert.Equal(0, harness.GrantReadCount);
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
        Assert.Equal(0, harness.CommitCount);
        Assert.Equal(1, harness.FenceExecutionCount);
    }

    private static CapabilityAdmissionSnapshot CreateCapabilitySnapshot(
        CapabilityDependencyManifest requirements,
        CapabilityId allowedCapabilityId)
    {
        var descriptor = AuthorityGrantApplicationTestFixture.Capability(allowedCapabilityId.Value);
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var provider, out _));
        Assert.True(CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _));
        var requirement = Assert.Single(requirements.Required);
        var pin = new CapabilityAdmissionPin(
            descriptor,
            CapabilityKind.GraphNode,
            new CapabilityImplementationIdentity(provider!, "governed-loop-test"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/capabilities", "1", null),
            new CapabilityDependencyArtifactMetadata(null, null),
            "A safe governed-loop test capability.");
        var evidence = new CapabilityAdmissionEvidence(
            requirements.SubjectId,
            allowedCapabilityId,
            requirement.CompatibleVersionRange,
            false,
            "Selected",
            descriptor,
            "A server-verified installed and available catalog candidate was selected.");
        return new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            requirements,
            requirementsHash!.Value,
            [pin],
            [evidence],
            AuthorityGrantApplicationTestFixture.Now.AddMinutes(1));
    }

    private static GovernedLoopAdmissionTerminalOutcome RebindWorkspace(
        GovernedLoopAdmissionTerminalOutcome source,
        string workspaceId)
    {
        var receipt = Assert.IsType<GovernedLoopAdmissionReceipt>(source.Receipt);
        var intent = source.Intent with { WorkspaceId = workspaceId };
        var snapshot = receipt.Evidence.CapabilityAdmission with { WorkspaceScopeId = workspaceId };
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            receipt.Evidence.SchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            receipt.Evidence.Binding,
            receipt.Evidence.GrantProfile,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.GrantDependencyEvidenceHash,
            receipt.Evidence.EffectiveAuthority,
            snapshot,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, receipt.Evidence.EffectiveAuthority, snapshot),
            receipt.Evidence.EvaluatedAtUtc,
            string.Empty));
        var reboundReceipt = GovernedLoopAdmissionContractHash.Apply(receipt with
        {
            Intent = intent,
            Evidence = evidence,
            ContentHash = string.Empty,
        });
        return GovernedLoopAdmissionContractHash.Apply(source with
        {
            Intent = intent,
            Receipt = reboundReceipt,
            ContentHash = string.Empty,
        });
    }
}
