using System.Collections.Immutable;
using System.Reflection;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewAdmissionServiceTests
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Admit_commits_the_exact_blocked_frontier_request_lifecycle_evidence_and_pause_transition_in_one_cas()
    {
        var fixture = await CreateFixtureAsync();
        var predecessorValidation = CustomLoopRunValidator.Validate(fixture.Predecessor);
        Assert.True(predecessorValidation.IsValid, string.Join(Environment.NewLine, predecessorValidation.Errors));
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, fixture.BlockedFrontier));

        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
        Assert.Equal(1, shared.UpdateCount);
        var persisted = Assert.IsType<CustomLoopRunRecord>(shared.Run);
        Assert.Equal(CustomLoopRunStatus.Paused, persisted.Status);
        Assert.Equal(fixture.BlockedFrontier.Payload.ContentHash, persisted.Frontier!.Payload.ContentHash);
        Assert.Equal(fixture.Request.RequestHash, persisted.HumanReview!.Request.RequestHash);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, persisted.HumanReview.Lifecycle.Status);
        Assert.Equal(2, persisted.Events.Length - fixture.Predecessor.Events.Length);
        Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, persisted.Events[^2].Kind);
        Assert.Equal(CustomLoopRunEventKind.HumanReviewRequestAdmitted, persisted.Events[^1].Kind);
        Assert.Equal(persisted.HumanReview.Evidence.Single().EvidenceHash, persisted.Events[^1].HumanReviewEvidence?.EvidenceHash);
        Assert.Null(persisted.ExecutionClock.ActiveSinceUtc);
        Assert.Equal(120_000, persisted.ExecutionClock.AccumulatedRunningMilliseconds);

        var restart = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var replay = await restart.AdmitAsync(new HumanReviewAdmissionCommand(persisted.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, fixture.BlockedFrontier));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, replay.Status);
        var divergent = Reissue(fixture.Request, "review-request-divergent", fixture.Request.RequestOperationId);
        var conflict = await restart.AdmitAsync(new HumanReviewAdmissionCommand(persisted.Id, fixture.Predecessor.LifecycleVersion, divergent, fixture.BlockedFrontier));
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, conflict.Status);
        Assert.Equal(1, shared.UpdateCount);
    }

    [Theory]
    [InlineData("executable-hash")]
    [InlineData("revision-id")]
    [InlineData("blocked-before-request")]
    [InlineData("old-blocked-intermediate")]
    [InlineData("max-lifecycle-version")]
    [InlineData("stale-lifecycle")]
    public async Task Admit_fails_closed_without_mutation_when_any_exact_frontier_binding_or_predecessor_invariant_differs(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var predecessor = mutation == "old-blocked-intermediate"
            ? fixture.Predecessor with { Status = CustomLoopRunStatus.Paused, Frontier = fixture.BlockedFrontier, ExecutionClock = CustomLoopExecutionClock.NotStarted() }
            : mutation == "max-lifecycle-version" ? fixture.Predecessor with { LifecycleVersion = int.MaxValue }
            : fixture.Predecessor;
        var request = mutation == "executable-hash" ? Rebind(fixture.Request, revisionHash: Hash('f')) : fixture.Request;
        var blocked = mutation == "revision-id" ? CreateFrontier(fixture.Predecessor, "revision-other", Hash('c'), GovernedLoopFrontierStatus.ReviewBlocked, fixture.Predecessor.UpdatedAtUtc.AddMinutes(1))
            : mutation == "blocked-before-request" ? CreateFrontier(fixture.Predecessor, fixture.BlockedFrontier.Binding.Revision.RevisionId, fixture.BlockedFrontier.Binding.Revision.ExecutableHash, GovernedLoopFrontierStatus.ReviewBlocked, fixture.Request.Timing.CreatedAtUtc.AddMinutes(-1))
            : fixture.BlockedFrontier;
        var shared = new HumanReviewAdmissionSharedState(predecessor);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));

        var expectedLifecycleVersion = mutation == "stale-lifecycle" ? predecessor.LifecycleVersion - 1 : predecessor.LifecycleVersion;
        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(predecessor.Id, expectedLifecycleVersion, request, blocked));

        Assert.Equal(CustomLoopRunStoreStatus.Conflict, result.Status);
        Assert.Equal(0, shared.UpdateCount);
        Assert.Null(shared.Run!.HumanReview);
    }

    [Fact]
    public async Task Admit_returns_not_found_when_the_canonical_run_has_disappeared()
    {
        var fixture = await CreateFixtureAsync();
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor) { Run = null };
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, fixture.BlockedFrontier));

        Assert.Equal(CustomLoopRunStoreStatus.NotFound, result.Status);
        Assert.Equal(0, shared.UpdateCount);
    }

    [Fact]
    public async Task Admit_rejects_a_request_with_a_tampered_integrity_hash_before_rebuilding_the_frontier()
    {
        var fixture = await CreateFixtureAsync();
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var tampered = fixture.Request with { RequestHash = Hash('z') };

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, tampered, fixture.BlockedFrontier));

        Assert.Equal(CustomLoopRunStoreStatus.Conflict, result.Status);
        Assert.Equal(0, shared.UpdateCount);
        Assert.Null(shared.Run!.HumanReview);
    }

    [Fact]
    public async Task Admit_rejects_a_review_blocked_event_that_does_not_match_the_exact_blocked_activation()
    {
        var fixture = await CreateFixtureAsync();
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var mismatchedEvent = fixture.Predecessor.Events[^1] with
        {
            Kind = CustomLoopRunEventKind.NodeOutcomeObserved,
            StepId = "different-node",
        };

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, fixture.BlockedFrontier, mismatchedEvent));

        Assert.Equal(CustomLoopRunStoreStatus.Conflict, result.Status);
        Assert.Equal(0, shared.UpdateCount);
        Assert.Null(shared.Run!.HumanReview);
    }

    [Fact]
    public async Task Admit_fails_closed_when_a_corrupt_frontier_snapshot_cannot_be_rebound()
    {
        var fixture = await CreateFixtureAsync();
        var constructor = typeof(GovernedLoopFrontierPosture).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(GovernedLoopExecutionBinding), typeof(string), typeof(string), typeof(string), typeof(GovernedLoopFrontierPayload)], null);
        Assert.NotNull(constructor);
        var corruptFrontier = Assert.IsType<GovernedLoopFrontierPosture>(constructor!.Invoke([string.Empty, fixture.BlockedFrontier.Binding, fixture.BlockedFrontier.GraphArtifactHash, fixture.BlockedFrontier.GraphLayoutHash, fixture.BlockedFrontier.AdmissionReceiptHash, fixture.BlockedFrontier.Payload]));
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, corruptFrontier));

        Assert.Equal(CustomLoopRunStoreStatus.Conflict, result.Status);
        Assert.Equal(0, shared.UpdateCount);
        Assert.Null(shared.Run!.HumanReview);
    }

    [Fact]
    public async Task Admit_fails_closed_when_the_current_run_shape_throws_during_validation()
    {
        var fixture = await CreateFixtureAsync();
        var malformed = fixture.Predecessor with { ExecutionClock = null! };
        var shared = new HumanReviewAdmissionSharedState(malformed);
        var service = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));

        var result = await service.AdmitAsync(new HumanReviewAdmissionCommand(malformed.Id, malformed.LifecycleVersion, fixture.Request, fixture.BlockedFrontier));

        Assert.Equal(CustomLoopRunStoreStatus.Conflict, result.Status);
        Assert.Equal(0, shared.UpdateCount);
    }

    [Fact]
    public async Task Concurrent_distinct_admissions_across_store_instances_have_exactly_one_atomic_winner()
    {
        var fixture = await CreateFixtureAsync();
        var shared = new HumanReviewAdmissionSharedState(fixture.Predecessor, gateInitialReads: true);
        var first = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var second = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var distinct = Reissue(fixture.Request, "review-request-two", "review-request-operation-two");

        var results = await Task.WhenAll(
            first.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, fixture.Request, fixture.BlockedFrontier)),
            second.AdmitAsync(new HumanReviewAdmissionCommand(fixture.Predecessor.Id, fixture.Predecessor.LifecycleVersion, distinct, fixture.BlockedFrontier)));

        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Updated);
        Assert.Single(results, result => result.Status == CustomLoopRunStoreStatus.Conflict);
        Assert.Equal(1, shared.UpdateCount);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(shared.Run)).IsValid);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var materializedStore = new GovernedLoopSequentialRunMaterializerTests.RecordingRunStore();
        var materializer = new GovernedLoopSequentialRunMaterializer(
            materializedStore,
            new GovernedLoopSequentialRunMaterializerTests.RecordingAuditRecorder(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingEventIdentityGenerator(),
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(_createdAtUtc));
        var materialized = await materializer.MaterializeAsync(context.Request);
        var admitted = Assert.IsType<CustomLoopRunRecord>(materialized.Run);
        var active = TransitionToRunning(admitted);
        var running = StartInference(active, context);
        var block = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(running.Frontier, context.AdapterBinding, null, null, running.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, block.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(block.Frontier);
        Assert.True(CustomLoopRunValidator.Validate(running).IsValid);
        return new Fixture(running, blocked, Request(running, blocked));
    }

    private static GovernedLoopFrontierPosture CreateFrontier(GovernedLoopExecutionBinding binding, string workspaceId, GovernedLoopFrontierStatus status, DateTimeOffset updatedAtUtc)
    {
        var nodeStatus = status == GovernedLoopFrontierStatus.ReviewBlocked ? GovernedLoopNodeExecutionStatus.ReviewBlocked : GovernedLoopNodeExecutionStatus.Running;
        var node = GovernedLoopNodeExecutionEvidence.Create(0, "review-node", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, "human-review", 1), [], [], nodeStatus, 1, "attempt-one");
        return GovernedLoopFrontierPosture.Create(binding, workspaceId, Hash('a'), Hash('b'), Hash('d'), status == GovernedLoopFrontierStatus.ReviewBlocked ? 2 : 1, 1, status, [node], updatedAtUtc, string.Empty);
    }

    private static GovernedLoopFrontierPosture CreateFrontier(CustomLoopRunRecord predecessor, string revisionId, string executableHash, GovernedLoopFrontierStatus status, DateTimeOffset updatedAtUtc)
    {
        var retained = Assert.IsType<GovernedLoopFrontierPosture>(predecessor.Frontier);
        var revision = GovernedLoopRevisionReference.Create(1, retained.Binding.Revision.GraphId, revisionId, executableHash);
        return CreateFrontier(GovernedLoopExecutionBinding.Create(1, predecessor.Id, revision, 1), retained.WorkspaceId, status, updatedAtUtc);
    }

    private static HumanReviewRequest Request(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked)
    {
        var blockedNode = Assert.Single(blocked.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(1, blocked.WorkspaceId, predecessor.Id, blocked.Binding.Revision.GraphId, blocked.Binding.Revision.RevisionId, blocked.Binding.Revision.ExecutableHash, blockedNode.NodeId, blockedNode.ActivationOrdinal, null, blockedNode.Attempt!.Value, "frontier-one", blocked.Payload.FrontierVersion, blocked.Payload.ContentHash, Hash('e'), Hash('f'), Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'), null, string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, null, string.Empty));
        var timing = new HumanReviewTiming(predecessor.UpdatedAtUtc, predecessor.UpdatedAtUtc.AddMinutes(10), predecessor.UpdatedAtUtc.AddHours(1));
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(1, "review-request-one", "review-request-operation-one", binding, HumanReviewPurpose.Continuation, ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation), ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"))), scope, ImmutableArray.Create(
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)),
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)),
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))), timing, Provenance(HumanReviewProvenanceKind.Server, "request-correlation", timing.CreatedAtUtc), string.Empty));
    }

    private static CustomLoopRunRecord TransitionToRunning(CustomLoopRunRecord admitted)
    {
        var updatedAtUtc = admitted.UpdatedAtUtc.AddMinutes(1);
        var lifecycle = new CustomLoopRunEvent(
            admitted.Events[^1].Sequence + 1,
            "event-running",
            updatedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "Run entered its canonical running lifecycle.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ControlExpectedLifecycleVersion: admitted.LifecycleVersion);
        return admitted with
        {
            LifecycleVersion = admitted.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = updatedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, updatedAtUtc),
            Events = [.. admitted.Events, lifecycle],
        };
    }

    private static CustomLoopRunRecord StartInference(CustomLoopRunRecord active, GovernedLoopSequentialRunMaterializerTests.TestContext context)
    {
        var node = context.Plan.Nodes[1];
        var activation = active.Frontier!.Payload.Nodes.Single(candidate => string.Equals(candidate.NodeId, node.NodeId, StringComparison.Ordinal));
        var updatedAtUtc = active.UpdatedAtUtc.AddMinutes(1);
        var start = new CustomLoopRunEvent(
            active.Events[^1].Sequence + 1,
            "event-review-admission-start",
            updatedAtUtc,
            CustomLoopRunEventKind.NodeAttemptStarted,
            1,
            node.NodeId,
            1,
            "The exact canonical node attempt started.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            TraceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            context.AdapterBinding.WorkspaceId,
            context.AdapterBinding.ExecutionBinding.RunId,
            context.AdapterBinding.ExecutionBinding.Revision,
            context.AdapterBinding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            1,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            null,
            null,
            CustomLoopSequentialNodeDisposition.Unknown,
            CustomLoopSequentialOutcomeArtifactHash.Compute(start),
            string.Empty));
        var transition = GovernedLoopSequentialFrontierMachine.Start(active.Frontier, context.AdapterBinding, context.Plan, node, activation, 1, start.EventId, updatedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        return active with
        {
            LifecycleVersion = active.LifecycleVersion + 1,
            UpdatedAtUtc = updatedAtUtc,
            Frontier = Assert.IsType<GovernedLoopFrontierPosture>(transition.Frontier),
            Events = [.. active.Events, start with { SequentialNodeEvidence = evidence }],
        };
    }

    private static HumanReviewRequest Rebind(HumanReviewRequest request, string? revisionHash = null, string? nodeId = null)
    {
        var binding = HumanReviewContractHash.ApplyBinding(request.Binding with { RevisionHash = revisionHash ?? request.Binding.RevisionHash, NodeId = nodeId ?? request.Binding.NodeId, BindingHash = string.Empty });
        var scope = HumanReviewContractHash.ApplyApprovalScope(request.ApprovalScope with { BindingHash = binding.BindingHash, ScopeHash = string.Empty });
        return HumanReviewContractHash.ApplyRequest(request with { Binding = binding, ApprovalScope = scope, RequestHash = string.Empty });
    }

    private static HumanReviewRequest Reissue(HumanReviewRequest request, string requestId, string operationId)
        => HumanReviewContractHash.ApplyRequest(request with
        {
            RequestId = requestId,
            RequestOperationId = operationId,
            Provenance = request.Provenance with { CorrelationId = operationId, ProvenanceHash = string.Empty },
            RequestHash = string.Empty,
        });

    private static HumanReviewProvenance Provenance(HumanReviewProvenanceKind kind, string correlationId, DateTimeOffset atUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(kind, "human-review-store", correlationId, atUtc, string.Empty));

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private static string WorkspaceId => "workspace-sha256:" + Hash('a');

    private sealed record Fixture(CustomLoopRunRecord Predecessor, GovernedLoopFrontierPosture BlockedFrontier, HumanReviewRequest Request);

}
