using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal static class GovernedLoopSleepApplicationTestFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 11, 22, 0, 0, TimeSpan.Zero);

    internal static string Hash(char value) => new(value, GovernedLoopSleepContractLimits.Sha256HexCharacters);

    internal static GovernedLoopExecutionBinding Binding(long generation = 1)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "sleep-graph", "sleep-revision", Hash('a'));
        return GovernedLoopExecutionBinding.Create(1, "sleep-run", revision, generation);
    }

    internal static GovernedLoopRevisionPublicationPin Publication(
        GovernedLoopExecutionBinding binding,
        string operationId = "publish-sleep-revision")
        => new(1, binding.Revision, operationId, Hash('b'));

    internal static GovernedLoopNodeExecutionEvidence WaitingNode(
        int activationOrdinal = 0,
        int visitOrdinal = 1,
        int waitAttempt = 1,
        string waitOperationId = "wait-operation-1",
        string? cycleId = null,
        int? cycleIteration = null)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            0,
            visitOrdinal,
            "wait-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, "wait-timestamp", 1),
            ["edge-trigger-wait"],
            ["edge-wait-exit"],
            GovernedLoopNodeExecutionStatus.Waiting,
            waitAttempt,
            waitOperationId,
            cycleId: cycleId,
            cycleIteration: cycleIteration);

    internal static GovernedLoopSleepCurrentPosture Posture(
        GovernedLoopExecutionBinding? binding = null,
        GovernedLoopRevisionPublicationPin? publication = null,
        GovernedLoopNodeExecutionEvidence? node = null,
        long frontierVersion = 7,
        GovernedLoopRunStatus lifecycleStatus = GovernedLoopRunStatus.Waiting,
        bool unattended = true,
        DateTimeOffset? expiresAtUtc = null,
        IEnumerable<GovernedLoopEffectPosture>? effects = null,
        DateTimeOffset? observedAtUtc = null,
        GovernedLoopFrontierStatus? frontierStatus = null,
        IEnumerable<GovernedLoopNodeExecutionEvidence>? nodes = null)
    {
        var selectedBinding = binding ?? Binding();
        var selectedPublication = publication ?? Publication(selectedBinding);
        var selectedNode = node ?? WaitingNode();
        var selectedFrontierStatus = frontierStatus ?? lifecycleStatus switch
        {
            GovernedLoopRunStatus.Cancelled => GovernedLoopFrontierStatus.Cancelled,
            GovernedLoopRunStatus.NeedsReview => GovernedLoopFrontierStatus.ReviewBlocked,
            _ => GovernedLoopFrontierStatus.Waiting
        };
        if (lifecycleStatus == GovernedLoopRunStatus.NeedsReview)
        {
            selectedNode = GovernedLoopNodeExecutionEvidence.CreateActivation(
                selectedNode.ActivationOrdinal,
                selectedNode.PlanOrdinal,
                selectedNode.VisitOrdinal,
                selectedNode.NodeId,
                selectedNode.Descriptor,
                selectedNode.IncomingControlEdgeIds,
                selectedNode.OutgoingControlEdgeIds,
                GovernedLoopNodeExecutionStatus.ReviewBlocked,
                selectedNode.Attempt,
                selectedNode.AttemptOperationId,
                cycleId: selectedNode.CycleId,
                cycleIteration: selectedNode.CycleIteration);
        }

        var updatedAtUtc = Now.AddMinutes(-1);
        var lifecycle = GovernedLoopRunLifecycle.Create(
            selectedBinding,
            GovernedLoopRunLifecyclePayload.Create(
                1,
                3,
                lifecycleStatus,
                Now.AddHours(-1),
                updatedAtUtc,
                GovernedLoopExecutionStateMatrix.IsTerminal(lifecycleStatus) ? updatedAtUtc : null));
        var frontier = GovernedLoopFrontierPosture.Create(
            selectedBinding,
            "workspace-sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Hash('c'),
            Hash('d'),
            Hash('e'),
            frontierVersion,
            GovernedLoopExecutionLimits.Schema1ConcurrencyCeiling,
            selectedFrontierStatus,
            nodes ?? [selectedNode],
            updatedAtUtc,
            string.Empty);
        var evidence = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, effects ?? [], []);
        return new GovernedLoopSleepCurrentPosture(
            evidence,
            selectedPublication,
            unattended,
            Hash('f'),
            expiresAtUtc,
            observedAtUtc ?? Now,
            Hash('9'));
    }

    internal static GovernedLoopNodeExecutionEvidence ReadyNode(int activationOrdinal = 1)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            1,
            1,
            "ready-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "transform-ready", 1),
            ["edge-trigger-ready"],
            ["edge-ready-exit"],
            GovernedLoopNodeExecutionStatus.Ready);

    internal static GovernedLoopNodeExecutionEvidence RunningNode(int activationOrdinal = 1)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            1,
            1,
            "ready-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "transform-ready", 1),
            ["edge-trigger-ready"],
            ["edge-ready-exit"],
            GovernedLoopNodeExecutionStatus.Running,
            1,
            "ready-operation-1");

    internal static GovernedLoopNodeExecutionEvidence CompletedWaitNode()
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "wait-node",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, "wait-timestamp", 1),
            ["edge-trigger-wait"],
            ["edge-wait-exit"],
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            "wait-operation-1",
            "wait-outcome-1",
            Hash('5'),
            "cycle-visit",
            1);

    internal static GovernedLoopEffectPosture OpenEffect(GovernedLoopExecutionBinding binding)
        => GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopEffectPayload.Create(
                1,
                "provider-effect",
                "provider-operation",
                1,
                GovernedLoopEffectOrigin.Provider,
                "wait-node",
                Hash('8'),
                GovernedLoopEffectPhase.DispatchBoundaryReached,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                null,
                null,
                Now.AddMinutes(-1)));

    internal static GovernedLoopSleepPublicationRequest PublicationRequest(
        GovernedLoopSleepCurrentPosture posture,
        GovernedLoopWakeMode mode = GovernedLoopWakeMode.Timestamp,
        DateTimeOffset? deadlineUtc = null,
        string? eventReference = null)
    {
        var node = posture.Execution.Frontier.Payload.Nodes[0];
        var binding = new GovernedLoopSleepBinding(
            posture.Execution.Lifecycle.Binding,
            posture.Publication,
            posture.Execution.Frontier.Payload.FrontierVersion,
            posture.Execution.Frontier.Payload.ContentHash,
            node.ActivationOrdinal,
            node.CycleId,
            node.CycleIteration,
            node.NodeId,
            node.VisitOrdinal,
            node.Attempt!.Value,
            node.AttemptOperationId!);
        return new GovernedLoopSleepPublicationRequest(
            binding,
            mode,
            mode == GovernedLoopWakeMode.Timestamp ? deadlineUtc ?? Now : null,
            mode == GovernedLoopWakeMode.AuthenticatedEvent ? eventReference ?? "event-subscription-1" : null);
    }

    internal static GovernedLoopWakeIdentity WakeIdentity(
        GovernedLoopSleepCheckpoint checkpoint,
        string? authenticationEvidenceHash = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            1,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            checkpoint.AuthenticatedEventReference,
            checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent
                ? authenticationEvidenceHash ?? Hash('7')
                : null,
            string.Empty));

    internal static GovernedLoopWakeEvidence Prepared(
        GovernedLoopSleepCheckpoint checkpoint,
        string operationId = "continuation-operation-1",
        string? authenticationEvidenceHash = null,
        long evidenceVersion = 1,
        DateTimeOffset? recordedAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            1,
            evidenceVersion,
            WakeIdentity(checkpoint, authenticationEvidenceHash),
            GovernedLoopWakeDisposition.Prepared,
            operationId,
            null,
            null,
            recordedAtUtc ?? Now,
            string.Empty));

    internal static GovernedLoopWakeEvidence Ambiguous(
        GovernedLoopWakeEvidence current,
        DateTimeOffset? recordedAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(current with
        {
            EvidenceVersion = current.EvidenceVersion + 1,
            Disposition = GovernedLoopWakeDisposition.AmbiguousAttempt,
            ContinuationEvidenceHash = null,
            DispositionEvidenceReference = "ambiguous-continuation-evidence",
            RecordedAtUtc = recordedAtUtc ?? current.RecordedAtUtc.AddTicks(1),
            ContentHash = string.Empty
        });

    internal static GovernedLoopWakeEvidence Terminal(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeDisposition disposition,
        string? authenticationEvidenceHash = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            1,
            1,
            WakeIdentity(checkpoint, authenticationEvidenceHash),
            disposition,
            disposition == GovernedLoopWakeDisposition.Committed ? "continuation-operation-1" : null,
            disposition == GovernedLoopWakeDisposition.Committed ? Hash('6') : null,
            disposition == GovernedLoopWakeDisposition.Committed ? null : "terminal-wake-evidence",
            Now,
            string.Empty));
}
