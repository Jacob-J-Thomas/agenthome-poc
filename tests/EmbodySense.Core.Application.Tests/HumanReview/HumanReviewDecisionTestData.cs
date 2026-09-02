using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal static class HumanReviewDecisionTestData
{
    public static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public static async Task<HumanReviewDecisionTestFixture> CreateAsync(ImmutableArray<HumanReviewDecisionKind>? requestedDecisions = null, bool includeEffectAttempt = false, long effectAttemptExecutionGenerationOffset = 0)
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var materializedStore = new GovernedLoopSequentialRunMaterializerTests.RecordingRunStore();
        var materializer = new GovernedLoopSequentialRunMaterializer(
            materializedStore,
            new GovernedLoopSequentialRunMaterializerTests.RecordingAuditRecorder(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingEventIdentityGenerator(),
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(CreatedAtUtc));
        var admitted = Assert.IsType<CustomLoopRunRecord>((await materializer.MaterializeAsync(context.Request)).Run);
        var active = TransitionToRunning(admitted);
        var running = StartInference(active, context);
        var block = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(running.Frontier, context.AdapterBinding, null, null, running.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, block.Status);
        var blocked = Assert.IsType<GovernedLoopFrontierPosture>(block.Frontier);
        var request = Request(running, blocked, requestedDecisions, includeEffectAttempt, effectAttemptExecutionGenerationOffset, out var effectAttempt);
        var shared = new HumanReviewAdmissionSharedState(running);
        var admission = new HumanReviewAdmissionService(new HumanReviewAdmissionTestStore(shared));
        var admissionResult = await admission.AdmitAsync(new HumanReviewAdmissionCommand(running.Id, running.LifecycleVersion, request, blocked));
        Assert.Equal(EmbodySense.Core.Application.Loops.Models.CustomLoopRunStoreStatus.Updated, admissionResult.Status);
        return new HumanReviewDecisionTestFixture(Assert.IsType<CustomLoopRunRecord>(shared.Run), request, effectAttempt);
    }

    public static HumanReviewDecisionCommand Command(CustomLoopRunRecord run, string operationId, HumanReviewDecisionKind kind, string? detail = null, int? expectedLifecycleVersion = null)
        => new(run.Id, expectedLifecycleVersion ?? run.LifecycleVersion, operationId, kind, detail);

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

    private static HumanReviewRequest Request(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked, ImmutableArray<HumanReviewDecisionKind>? requestedDecisions, bool includeEffectAttempt, long effectAttemptExecutionGenerationOffset, out GovernedLoopEffectAttempt? effectAttempt)
    {
        var blockedNode = Assert.Single(blocked.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(1, blocked.WorkspaceId, predecessor.Id, blocked.Binding.Revision.GraphId, blocked.Binding.Revision.RevisionId, blocked.Binding.Revision.ExecutableHash, blockedNode.NodeId, blockedNode.ActivationOrdinal, null, blockedNode.Attempt!.Value, "frontier-one", blocked.Payload.FrontierVersion, blocked.Payload.ContentHash, Hash('e'), Hash('f'), Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'), null, string.Empty));
        effectAttempt = null;
        if (includeEffectAttempt)
        {
            effectAttempt = CreateEffectAttempt(predecessor, binding, effectAttemptExecutionGenerationOffset);
            var preparation = HumanReviewEffectReleaseContract.CreatePreparation(binding, effectAttempt);
            var reviewedEffect = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(effectAttempt.Payload.EffectId, effectAttempt.Payload.OperationId, effectAttempt.Payload.EffectGeneration, effectAttempt.Payload.IntentHash, preparation.PreparationHash, HumanReviewEffectDispatchCertainty.NotDispatched, string.Empty));
            binding = HumanReviewContractHash.ApplyBinding(binding with { EffectAttempt = reviewedEffect, BindingHash = string.Empty });
        }
        var purpose = includeEffectAttempt ? HumanReviewPurpose.PreDispatchEffect : HumanReviewPurpose.Continuation;
        var scopeKind = includeEffectAttempt ? HumanReviewApprovalScopeKind.PreDispatchEffect : HumanReviewApprovalScopeKind.Continuation;
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(scopeKind, binding.BindingHash, binding.EffectAttempt?.EffectAttemptId, string.Empty));
        var timing = new HumanReviewTiming(predecessor.UpdatedAtUtc, predecessor.UpdatedAtUtc.AddMinutes(10), predecessor.UpdatedAtUtc.AddHours(1));
        var decisions = requestedDecisions ?? ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation);
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(1, "review-request-one", "review-request-operation-one", binding, purpose, decisions, ImmutableArray.Create(new HumanReviewReviewerScope("reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"))), scope, ImmutableArray.Create(
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)),
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)),
            HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))), timing, Provenance(HumanReviewProvenanceKind.Server, "request-correlation", timing.CreatedAtUtc), string.Empty));
    }

    private static GovernedLoopEffectAttempt CreateEffectAttempt(CustomLoopRunRecord predecessor, HumanReviewBinding binding, long executionGenerationOffset)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/workspace/read-file", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.2.3", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('a'), out var capabilityHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var adapter = Assert.IsType<GovernedLoopSequentialAdapterBinding>(predecessor.SequentialAdapterBinding);
        return GovernedLoopEffectAttemptContract.Prepare(
            GovernedLoopExecutionBinding.Create(
                adapter.ExecutionBinding.SchemaVersion,
                adapter.ExecutionBinding.RunId,
                adapter.ExecutionBinding.Revision,
                checked(adapter.ExecutionBinding.ExecutionGeneration + executionGenerationOffset)),
            binding.NodeId,
            binding.Attempt,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, capabilityHash!),
            new CapabilityImplementationIdentity(providerId!, "workspace/read-file"),
            "probe/observe",
            Hash('b'),
            "effect-continuation-one",
            "effect-continuation-operation-one",
            1,
            Hash('c'),
            Hash('d'),
            Hash('e'),
            adapter.AdmissionReceipt.ContentHash,
            "before-continuation-one",
            predecessor.UpdatedAtUtc.AddSeconds(1));
    }

    private static HumanReviewProvenance Provenance(HumanReviewProvenanceKind kind, string correlationId, DateTimeOffset atUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(kind, "human-review-store", correlationId, atUtc, string.Empty));

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);
}
