using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Defines executor-neutral legal state shapes and transitions without selecting retry, recovery, or reconciliation policy.</summary>
public static class GovernedLoopExecutionStateMatrix
{
    /// <summary>Determines whether a run status is supported by schema 1.</summary>
    /// <param name="status">The candidate status.</param>
    /// <returns><see langword="true"/> for a defined non-unknown status.</returns>
    public static bool IsSupported(GovernedLoopRunStatus status) => status != GovernedLoopRunStatus.Unknown && Enum.IsDefined(status);

    /// <summary>Determines whether a node status is supported by schema 1.</summary>
    /// <param name="status">The candidate status.</param>
    /// <returns><see langword="true"/> for a defined non-unknown status.</returns>
    public static bool IsSupported(GovernedLoopNodeExecutionStatus status) => status != GovernedLoopNodeExecutionStatus.Unknown && Enum.IsDefined(status);

    /// <summary>Determines whether a frontier status is supported by schema 1.</summary>
    /// <param name="status">The candidate status.</param>
    /// <returns><see langword="true"/> for a defined non-unknown status.</returns>
    public static bool IsSupported(GovernedLoopFrontierStatus status) => status != GovernedLoopFrontierStatus.Unknown && Enum.IsDefined(status);

    /// <summary>Determines whether an effect origin is supported by schema 1.</summary>
    /// <param name="origin">The candidate origin.</param>
    /// <returns><see langword="true"/> for a defined non-unknown origin.</returns>
    public static bool IsSupported(GovernedLoopEffectOrigin origin) => origin != GovernedLoopEffectOrigin.Unknown && Enum.IsDefined(origin);

    /// <summary>Determines whether a projection class is supported by schema 1.</summary>
    /// <param name="projectionClass">The candidate class.</param>
    /// <returns><see langword="true"/> for a defined non-unknown class.</returns>
    public static bool IsSupported(GovernedLoopProjectionClass projectionClass) => projectionClass != GovernedLoopProjectionClass.Unknown && Enum.IsDefined(projectionClass);

    /// <summary>Determines whether effect origin and node-reference presence preserve graph attribution.</summary>
    /// <param name="origin">The effect origin.</param>
    /// <param name="hasOriginNodeId">Whether an exact originating node identity is retained.</param>
    /// <returns><see langword="true"/> when node-owned effects are attributed and the run-scoped origins remain optionally attributable.</returns>
    /// <remarks>Provider, actuator, and memory-mutation effects are canonical graph-node work. Publication, notification, and system-job effects may be run-scoped or node-scoped.</remarks>
    public static bool IsEffectOriginNodeValid(GovernedLoopEffectOrigin origin, bool hasOriginNodeId)
    {
        if (!IsSupported(origin))
        {
            return false;
        }

        return origin is not (GovernedLoopEffectOrigin.Provider or GovernedLoopEffectOrigin.Actuator or GovernedLoopEffectOrigin.MemoryMutation) || hasOriginNodeId;
    }

    /// <summary>Determines whether a run status stops automatic execution and therefore requires terminal timestamp evidence.</summary>
    /// <param name="status">The run status.</param>
    /// <returns><see langword="true"/> for completed, failed, cancelled, or ambiguity-terminal needs-review posture.</returns>
    public static bool IsTerminal(GovernedLoopRunStatus status) => status is GovernedLoopRunStatus.Completed or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.Cancelled or GovernedLoopRunStatus.NeedsReview;

    /// <summary>Determines whether node attempt and outcome-reference presence match a node posture.</summary>
    /// <param name="status">The node posture.</param>
    /// <param name="attempt">The optional positive attempt.</param>
    /// <param name="hasAttemptOperation">Whether the selected attempt has a durable operation correlation.</param>
    /// <param name="hasOutcomeEvidence">Whether retained outcome evidence is identified.</param>
    /// <param name="hasOutcomeEvidenceHash">Whether the retained outcome evidence has an exact hash.</param>
    /// <returns><see langword="true"/> when the shape is legal.</returns>
    public static bool IsNodeEvidenceShapeValid(GovernedLoopNodeExecutionStatus status, int? attempt, bool hasAttemptOperation, bool hasOutcomeEvidence, bool hasOutcomeEvidenceHash)
    {
        if (!IsSupported(status) || hasOutcomeEvidence != hasOutcomeEvidenceHash)
        {
            return false;
        }

        return status switch
        {
            GovernedLoopNodeExecutionStatus.Ready => attempt is null && !hasAttemptOperation && !hasOutcomeEvidence,
            GovernedLoopNodeExecutionStatus.Skipped => attempt is null && !hasAttemptOperation && hasOutcomeEvidence,
            GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed => attempt is > 0 && hasAttemptOperation && hasOutcomeEvidence,
            GovernedLoopNodeExecutionStatus.ReviewBlocked => attempt is > 0 && hasAttemptOperation,
            _ => attempt is > 0 && hasAttemptOperation && !hasOutcomeEvidence
        };
    }

    /// <summary>Determines whether bounded node evidence matches an aggregate frontier posture.</summary>
    /// <param name="status">The aggregate frontier posture.</param>
    /// <param name="nodes">The retained node evidence.</param>
    /// <returns><see langword="true"/> when the aggregate posture honestly describes the nodes. Aggregate review may retain unchanged Ready work only when no node is Running.</returns>
    public static bool IsFrontierShapeValid(GovernedLoopFrontierStatus status, IReadOnlyList<GovernedLoopNodeExecutionEvidence>? nodes)
    {
        if (!IsSupported(status)
            || nodes is not { Count: > 0 }
            || nodes.Count > GovernedLoopExecutionLimits.MaxFrontierNodes
            || nodes.Any(static node => node is null))
        {
            return false;
        }

        return status switch
        {
            GovernedLoopFrontierStatus.Active => nodes.Any(node => node.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running),
            GovernedLoopFrontierStatus.Waiting => nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Waiting) && nodes.All(node => node.Status is not GovernedLoopNodeExecutionStatus.Ready and not GovernedLoopNodeExecutionStatus.Running and not GovernedLoopNodeExecutionStatus.ReviewBlocked),
            GovernedLoopFrontierStatus.ReviewBlocked => nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Running)
                && (nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked)
                    || nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Ready)),
            GovernedLoopFrontierStatus.Completed => nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Completed) && nodes.All(node => node.Status is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Skipped),
            GovernedLoopFrontierStatus.Failed => nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Failed) && nodes.All(node => node.Status is not GovernedLoopNodeExecutionStatus.Running and not GovernedLoopNodeExecutionStatus.Waiting and not GovernedLoopNodeExecutionStatus.ReviewBlocked),
            GovernedLoopFrontierStatus.Cancelled => true,
            _ => false
        };
    }

    /// <summary>Determines whether typed effect axes and evidence-reference presence form one legal schema-1 state.</summary>
    /// <param name="phase">The effect phase.</param>
    /// <param name="outcome">The external outcome.</param>
    /// <param name="evidenceStatus">The evidence-completion posture.</param>
    /// <param name="hasOutcomeEvidence">Whether outcome evidence is retained.</param>
    /// <param name="hasReconciliationEvidence">Whether reconciliation or human-disposition evidence is retained.</param>
    /// <returns><see langword="true"/> when the combination is legal.</returns>
    public static bool IsEffectStateValid(GovernedLoopEffectPhase phase, GovernedLoopEffectOutcome outcome, GovernedLoopEffectEvidenceStatus evidenceStatus, bool hasOutcomeEvidence, bool hasReconciliationEvidence)
    {
        if (phase == GovernedLoopEffectPhase.Unknown || !Enum.IsDefined(phase) || !Enum.IsDefined(outcome) || evidenceStatus == GovernedLoopEffectEvidenceStatus.Unknown || !Enum.IsDefined(evidenceStatus))
        {
            return false;
        }

        return phase switch
        {
            GovernedLoopEffectPhase.IntentPrepared => outcome == GovernedLoopEffectOutcome.None && evidenceStatus is GovernedLoopEffectEvidenceStatus.Pending or GovernedLoopEffectEvidenceStatus.Complete && !hasOutcomeEvidence && !hasReconciliationEvidence,
            GovernedLoopEffectPhase.DispatchNotStarted => outcome == GovernedLoopEffectOutcome.None && evidenceStatus == GovernedLoopEffectEvidenceStatus.Complete && !hasOutcomeEvidence && !hasReconciliationEvidence,
            GovernedLoopEffectPhase.DispatchBoundaryReached => outcome == GovernedLoopEffectOutcome.OutcomeUnknown && evidenceStatus is GovernedLoopEffectEvidenceStatus.Pending or GovernedLoopEffectEvidenceStatus.Incomplete && !hasOutcomeEvidence && !hasReconciliationEvidence,
            GovernedLoopEffectPhase.OutcomeObserved => IsObservedOutcome(outcome) && EvidenceMatchesObservedOutcome(outcome, evidenceStatus) && hasOutcomeEvidence && !hasReconciliationEvidence,
            GovernedLoopEffectPhase.Committed => outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed && evidenceStatus == GovernedLoopEffectEvidenceStatus.Complete && hasOutcomeEvidence && !hasReconciliationEvidence,
            GovernedLoopEffectPhase.ReconciliationRequired => IsReconciliationRequiredStateValid(outcome, evidenceStatus, hasOutcomeEvidence, hasReconciliationEvidence),
            GovernedLoopEffectPhase.Reconciled => IsReconciledStateValid(outcome, evidenceStatus, hasOutcomeEvidence, hasReconciliationEvidence),
            _ => false
        };
    }

    /// <summary>Determines whether a projection status legally composes with optimistic and committed version references.</summary>
    /// <param name="projectionClass">The projection class.</param>
    /// <param name="status">The projection posture.</param>
    /// <param name="hasExpectedVersion">Whether an optimistic-precondition version is retained.</param>
    /// <param name="hasCommittedVersion">Whether a committed target version is retained.</param>
    /// <param name="hasReconciliationEvidence">Whether explicit reconciliation or operator-disposition evidence is retained.</param>
    /// <returns><see langword="true"/> when the combination is legal.</returns>
    public static bool IsProjectionStateValid(
        GovernedLoopProjectionClass projectionClass,
        GovernedLoopProjectionStatus status,
        bool hasExpectedVersion,
        bool hasCommittedVersion,
        bool hasReconciliationEvidence)
    {
        if (!IsSupported(projectionClass) || status == GovernedLoopProjectionStatus.Unknown || !Enum.IsDefined(status))
        {
            return false;
        }

        return projectionClass switch
        {
            GovernedLoopProjectionClass.LocalRuntime => (status is GovernedLoopProjectionStatus.Pending or GovernedLoopProjectionStatus.Committed)
                && !hasExpectedVersion
                && !hasCommittedVersion
                && !hasReconciliationEvidence,
            GovernedLoopProjectionClass.DurableReadModel => hasExpectedVersion && status switch
            {
                GovernedLoopProjectionStatus.Committed => hasCommittedVersion && !hasReconciliationEvidence,
                GovernedLoopProjectionStatus.Pending or GovernedLoopProjectionStatus.Conflict or GovernedLoopProjectionStatus.ReconciliationRequired => !hasCommittedVersion && !hasReconciliationEvidence,
                GovernedLoopProjectionStatus.Reconciled => hasReconciliationEvidence,
                _ => false
            },
            GovernedLoopProjectionClass.Surface => status switch
            {
                GovernedLoopProjectionStatus.Pending => !hasCommittedVersion && !hasReconciliationEvidence,
                GovernedLoopProjectionStatus.Committed => hasCommittedVersion && !hasReconciliationEvidence,
                GovernedLoopProjectionStatus.Conflict or GovernedLoopProjectionStatus.ReconciliationRequired => hasExpectedVersion && !hasCommittedVersion && !hasReconciliationEvidence,
                GovernedLoopProjectionStatus.Reconciled => hasExpectedVersion && hasReconciliationEvidence,
                _ => false
            },
            _ => false
        };
    }

    /// <summary>Determines whether a run lifecycle may transition directly to a successor status.</summary>
    /// <param name="current">The current status.</param>
    /// <param name="next">The proposed successor status.</param>
    /// <returns><see langword="true"/> for an idempotent or explicitly legal edge.</returns>
    public static bool IsRunTransitionAllowed(GovernedLoopRunStatus current, GovernedLoopRunStatus next)
    {
        if (!IsSupported(current) || !IsSupported(next))
        {
            return false;
        }

        if (IsTerminal(current))
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            GovernedLoopRunStatus.Admitted => next is GovernedLoopRunStatus.Running or GovernedLoopRunStatus.Waiting or GovernedLoopRunStatus.PauseRequested or GovernedLoopRunStatus.Paused or GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Cancelled or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            GovernedLoopRunStatus.Running => next is GovernedLoopRunStatus.Waiting or GovernedLoopRunStatus.PauseRequested or GovernedLoopRunStatus.Paused or GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Completed or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            GovernedLoopRunStatus.Waiting => next is GovernedLoopRunStatus.Running or GovernedLoopRunStatus.PauseRequested or GovernedLoopRunStatus.Paused or GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            GovernedLoopRunStatus.PauseRequested => next is GovernedLoopRunStatus.Paused or GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Completed or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            GovernedLoopRunStatus.Paused => next is GovernedLoopRunStatus.Running or GovernedLoopRunStatus.Waiting or GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Cancelled or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            GovernedLoopRunStatus.CancelRequested => next is GovernedLoopRunStatus.Cancelled or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.NeedsReview,
            _ => false
        };
    }

    /// <summary>Determines whether a node execution posture may transition directly to a successor posture.</summary>
    /// <param name="current">The current node posture.</param>
    /// <param name="next">The proposed successor posture.</param>
    /// <returns><see langword="true"/> for an idempotent or explicitly legal edge.</returns>
    public static bool IsNodeTransitionAllowed(GovernedLoopNodeExecutionStatus current, GovernedLoopNodeExecutionStatus next)
    {
        if (!IsSupported(current) || !IsSupported(next))
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            GovernedLoopNodeExecutionStatus.Ready => next is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Skipped or GovernedLoopNodeExecutionStatus.Failed,
            GovernedLoopNodeExecutionStatus.Running => next is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Waiting or GovernedLoopNodeExecutionStatus.Failed or GovernedLoopNodeExecutionStatus.ReviewBlocked,
            GovernedLoopNodeExecutionStatus.Waiting => next is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Failed or GovernedLoopNodeExecutionStatus.ReviewBlocked,
            GovernedLoopNodeExecutionStatus.ReviewBlocked => next is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.Failed,
            _ => false
        };
    }

    /// <summary>Determines whether one retained node-evidence item may directly replace its prior frontier posture.</summary>
    /// <param name="current">The current node evidence.</param>
    /// <param name="next">The proposed successor evidence for the same node.</param>
    /// <returns><see langword="true"/> when identity and incoming controls are immutable, the status edge is legal, and the same attempt is preserved.</returns>
    /// <remarks>Schema 1 intentionally defines no retry edge. A later retry policy must preserve prior committed outcomes and introduce an explicitly governed attempt or execution generation instead of reinterpreting this posture.</remarks>
    public static bool IsNodeEvidenceTransitionAllowed(GovernedLoopNodeExecutionEvidence? current, GovernedLoopNodeExecutionEvidence? next)
    {
        if (current is null || next is null
            || current.SchemaVersion != next.SchemaVersion
            || current.ActivationOrdinal != next.ActivationOrdinal
            || current.PlanOrdinal != next.PlanOrdinal
            || current.VisitOrdinal != next.VisitOrdinal
            || !string.Equals(current.NodeId, next.NodeId, StringComparison.Ordinal)
            || current.Descriptor != next.Descriptor
            || !current.IncomingControlEdgeIds.SequenceEqual(next.IncomingControlEdgeIds, StringComparer.Ordinal)
            || !current.OutgoingControlEdgeIds.SequenceEqual(next.OutgoingControlEdgeIds, StringComparer.Ordinal)
            || !string.Equals(current.CycleId, next.CycleId, StringComparison.Ordinal)
            || current.CycleIteration != next.CycleIteration
            || !SameJoinArrivals(current.JoinArrivals, next.JoinArrivals)
            || !IsNodeTransitionAllowed(current.Status, next.Status))
        {
            return false;
        }

        if (current.Status == next.Status)
        {
            return current.Attempt == next.Attempt
                && string.Equals(current.AttemptOperationId, next.AttemptOperationId, StringComparison.Ordinal)
                && string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)
                && string.Equals(current.OutcomeEvidenceHash, next.OutcomeEvidenceHash, StringComparison.Ordinal)
                && SameRoutingEvidence(current, next);
        }

        if (current.Status == GovernedLoopNodeExecutionStatus.Ready)
        {
            return next.Status switch
            {
                GovernedLoopNodeExecutionStatus.Skipped => next.Attempt is null
                    && next.ControlOutcome is null
                    && next.SelectedControlEdgeIds.Count == 0
                    && next.SkippedControlEdgeIds.Count == 0,
                GovernedLoopNodeExecutionStatus.Running => next.Attempt == 1 && next.AttemptOperationId is not null,
                GovernedLoopNodeExecutionStatus.Failed => next.Attempt == 1 && next.OutcomeEvidenceId is not null,
                _ => false
            };
        }

        return current.Attempt == next.Attempt
            && string.Equals(current.AttemptOperationId, next.AttemptOperationId, StringComparison.Ordinal)
            && current.OutcomeEvidenceId is null
            && current.ControlOutcome is null
            && current.SelectedControlEdgeIds.Count == 0
            && current.SkippedControlEdgeIds.Count == 0
            && (next.Status is not (GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed) || next.OutcomeEvidenceId is not null);
    }

    private static bool SameRoutingEvidence(GovernedLoopNodeExecutionEvidence current, GovernedLoopNodeExecutionEvidence next)
    {
        return current.ControlOutcome == next.ControlOutcome
            && current.SelectedControlEdgeIds.SequenceEqual(next.SelectedControlEdgeIds, StringComparer.Ordinal)
            && current.SkippedControlEdgeIds.SequenceEqual(next.SkippedControlEdgeIds, StringComparer.Ordinal);
    }

    private static bool SameJoinArrivals(IReadOnlyList<GovernedLoopJoinArrivalEvidence> current, IReadOnlyList<GovernedLoopJoinArrivalEvidence> next)
    {
        return current.Count == next.Count
            && current.Zip(next).All(pair => pair.First.SchemaVersion == pair.Second.SchemaVersion
                && pair.First.SourceActivationOrdinal == pair.Second.SourceActivationOrdinal
                && string.Equals(pair.First.ControlEdgeId, pair.Second.ControlEdgeId, StringComparison.Ordinal));
    }

    /// <summary>Determines whether an aggregate frontier may transition directly to a successor posture.</summary>
    /// <param name="current">The current frontier posture.</param>
    /// <param name="next">The proposed successor posture.</param>
    /// <returns><see langword="true"/> for an idempotent or explicitly legal edge.</returns>
    public static bool IsFrontierTransitionAllowed(GovernedLoopFrontierStatus current, GovernedLoopFrontierStatus next)
    {
        if (!IsSupported(current) || !IsSupported(next))
        {
            return false;
        }

        if (current is GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled)
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            GovernedLoopFrontierStatus.Active => next is GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked or GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled,
            GovernedLoopFrontierStatus.Waiting => next is GovernedLoopFrontierStatus.Active or GovernedLoopFrontierStatus.ReviewBlocked or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled,
            GovernedLoopFrontierStatus.ReviewBlocked => next is GovernedLoopFrontierStatus.Active or GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled,
            _ => false
        };
    }

    /// <summary>Identifies an initial prepared intent that may be considered for first dispatch by separate authority and policy.</summary>
    /// <param name="effect">The effect payload.</param>
    /// <returns><see langword="true"/> only for the initial <see cref="GovernedLoopEffectPhase.IntentPrepared"/> posture with no outcome or reconciliation evidence.</returns>
    /// <remarks>This value does not authorize dispatch and does not select retry or recovery policy. <see cref="GovernedLoopEffectPhase.DispatchNotStarted"/> is retained evidence of a prior dispatch decision and therefore returns <see langword="false"/>.</remarks>
    public static bool IsEffectDispatchEligible(GovernedLoopEffectPayload? effect)
    {
        return effect is not null
            && effect.Phase == GovernedLoopEffectPhase.IntentPrepared
            && effect.Outcome == GovernedLoopEffectOutcome.None
            && effect.ReconciliationEvidenceId is null;
    }

    /// <summary>Determines whether an effect phase may transition directly to another phase.</summary>
    /// <param name="current">The current phase.</param>
    /// <param name="next">The proposed successor phase.</param>
    /// <returns><see langword="true"/> for an idempotent or explicitly legal edge; committed, reconciliation-required, and reconciled phases never return to dispatch eligibility.</returns>
    public static bool IsEffectTransitionAllowed(GovernedLoopEffectPhase current, GovernedLoopEffectPhase next)
    {
        if (current == GovernedLoopEffectPhase.Unknown || next == GovernedLoopEffectPhase.Unknown || !Enum.IsDefined(current) || !Enum.IsDefined(next))
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            GovernedLoopEffectPhase.IntentPrepared => next is GovernedLoopEffectPhase.DispatchNotStarted or GovernedLoopEffectPhase.DispatchBoundaryReached or GovernedLoopEffectPhase.OutcomeObserved,
            GovernedLoopEffectPhase.DispatchNotStarted => next is GovernedLoopEffectPhase.DispatchBoundaryReached or GovernedLoopEffectPhase.OutcomeObserved,
            GovernedLoopEffectPhase.DispatchBoundaryReached => next is GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.ReconciliationRequired,
            GovernedLoopEffectPhase.OutcomeObserved => next is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired,
            GovernedLoopEffectPhase.ReconciliationRequired => next == GovernedLoopEffectPhase.Reconciled,
            _ => false
        };
    }

    /// <summary>Determines whether a projection posture may transition directly to a successor posture.</summary>
    /// <param name="current">The current posture.</param>
    /// <param name="next">The proposed successor posture.</param>
    /// <returns><see langword="true"/> for an idempotent or explicitly legal edge.</returns>
    public static bool IsProjectionTransitionAllowed(GovernedLoopProjectionStatus current, GovernedLoopProjectionStatus next)
    {
        if (current == GovernedLoopProjectionStatus.Unknown || next == GovernedLoopProjectionStatus.Unknown || !Enum.IsDefined(current) || !Enum.IsDefined(next))
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            GovernedLoopProjectionStatus.Pending => next is GovernedLoopProjectionStatus.Committed or GovernedLoopProjectionStatus.Conflict or GovernedLoopProjectionStatus.ReconciliationRequired,
            GovernedLoopProjectionStatus.Conflict => next == GovernedLoopProjectionStatus.ReconciliationRequired,
            GovernedLoopProjectionStatus.ReconciliationRequired => next == GovernedLoopProjectionStatus.Reconciled,
            _ => false
        };
    }

    private static bool IsObservedOutcome(GovernedLoopEffectOutcome outcome)
    {
        return outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed or GovernedLoopEffectOutcome.Conflicted;
    }

    private static bool EvidenceMatchesObservedOutcome(GovernedLoopEffectOutcome outcome, GovernedLoopEffectEvidenceStatus evidenceStatus)
    {
        return outcome == GovernedLoopEffectOutcome.Conflicted
            ? evidenceStatus == GovernedLoopEffectEvidenceStatus.Conflicting
            : evidenceStatus is GovernedLoopEffectEvidenceStatus.Complete or GovernedLoopEffectEvidenceStatus.Incomplete;
    }

    private static bool IsReconciliationRequiredStateValid(
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        bool hasOutcomeEvidence,
        bool hasReconciliationEvidence)
    {
        if (hasReconciliationEvidence)
        {
            return false;
        }

        return outcome switch
        {
            GovernedLoopEffectOutcome.OutcomeUnknown => evidenceStatus is GovernedLoopEffectEvidenceStatus.Incomplete or GovernedLoopEffectEvidenceStatus.Conflicting && !hasOutcomeEvidence,
            GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed => evidenceStatus == GovernedLoopEffectEvidenceStatus.Incomplete && hasOutcomeEvidence,
            GovernedLoopEffectOutcome.Conflicted => evidenceStatus is GovernedLoopEffectEvidenceStatus.Incomplete or GovernedLoopEffectEvidenceStatus.Conflicting && hasOutcomeEvidence,
            _ => false
        };
    }

    private static bool IsReconciledStateValid(
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        bool hasOutcomeEvidence,
        bool hasReconciliationEvidence)
    {
        return outcome != GovernedLoopEffectOutcome.None
            && evidenceStatus == GovernedLoopEffectEvidenceStatus.Complete
            && hasReconciliationEvidence
            && (outcome == GovernedLoopEffectOutcome.OutcomeUnknown ? !hasOutcomeEvidence : hasOutcomeEvidence);
    }
}
