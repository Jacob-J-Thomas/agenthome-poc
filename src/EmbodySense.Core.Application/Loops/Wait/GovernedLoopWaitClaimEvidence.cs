using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

internal static class GovernedLoopWaitClaimEvidence
{
    internal static IReadOnlyList<GovernedLoopNodeExecutionEvidence> FindExactRecoverableClaims(CustomLoopRunRecord run)
    {
        if (run.Status != CustomLoopRunStatus.Running
            || !CustomLoopRunValidator.Validate(run).IsValid
            || run.Frontier is null
            || run.SequentialAdapterBinding is null)
        {
            return [];
        }

        return run.Frontier.Payload.Nodes
            .Where(activation => IsExactRecoverableClaim(run, activation))
            .ToArray();
    }

    internal static bool IsExactRecoverableClaimStart(CustomLoopRunRecord run, CustomLoopRunEvent item)
        => run.Frontier?.Payload.Nodes.SingleOrDefault(activation =>
                activation.Status == GovernedLoopNodeExecutionStatus.Running
                && activation.Descriptor.Kind == GovernedLoopNodeKind.Wait
                && string.Equals(activation.AttemptOperationId, item.EventId, StringComparison.Ordinal)) is { } activation
            && IsExactWaitStart(run, activation, item)
            && IsExactRecoverableClaim(run, activation);

    internal static IReadOnlyList<GovernedLoopNodeExecutionEvidence> FindExactRecoverableContinuations(CustomLoopRunRecord run)
    {
        if (run.Status != CustomLoopRunStatus.Running
            || !CustomLoopRunValidator.Validate(run).IsValid
            || run.Frontier is null
            || run.SequentialAdapterBinding is null)
        {
            return [];
        }

        return run.Frontier.Payload.Nodes
            .Where(activation => IsExactRecoverableContinuation(run, activation))
            .ToArray();
    }

    internal static bool IsExactRecoverableContinuationStart(CustomLoopRunRecord run, CustomLoopRunEvent item)
        => run.Frontier?.Payload.Nodes.SingleOrDefault(activation =>
                activation.Status == GovernedLoopNodeExecutionStatus.Running
                && activation.Descriptor.Kind == GovernedLoopNodeKind.Wait
                && string.Equals(activation.AttemptOperationId, item.EventId, StringComparison.Ordinal)) is { } activation
            && IsExactWaitStart(run, activation, item)
            && IsExactRecoverableContinuation(run, activation);

    private static bool IsExactRecoverableClaim(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation)
    {
        if (run.WaitEvidence.Any(item => item.ActivationOrdinal == activation.ActivationOrdinal))
        {
            return false;
        }

        var starts = run.Events.Where(item => IsExactWaitStart(run, activation, item)).Take(2).ToArray();
        return starts.Length == 1 && !HasTerminalOutcome(run, activation, starts[0].Sequence);
    }

    private static bool IsExactWaitStart(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunEvent item)
    {
        if (run.Status != CustomLoopRunStatus.Running
            || run.SequentialAdapterBinding is not { } binding
            || activation is not
            {
                Status: GovernedLoopNodeExecutionStatus.Running,
                Descriptor.Kind: GovernedLoopNodeKind.Wait,
                Attempt: { } attempt,
                AttemptOperationId: { } attemptOperationId,
            }
            || item is not
            {
                Kind: CustomLoopRunEventKind.NodeAttemptStarted,
                Iteration: > 0,
                TraceReservationUtf8Bytes: CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes,
                SequentialNodeEvidence:
                {
                    Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
                    Disposition: CustomLoopSequentialNodeDisposition.Unknown,
                } evidence,
            })
        {
            return false;
        }

        return item.Sequence > run.Checkpoint.LastCommittedSequence
            && string.Equals(item.EventId, attemptOperationId, StringComparison.Ordinal)
            && item.Attempt == attempt
            && item.Iteration == (activation.CycleIteration ?? run.Checkpoint.Iteration)
            && string.Equals(item.StepId, activation.NodeId, StringComparison.Ordinal)
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && evidence.Attempt == attempt
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration
            && string.Equals(evidence.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal)
            && Equals(evidence.Revision, binding.ExecutionBinding.Revision)
            && evidence.ExecutionGeneration == binding.ExecutionBinding.ExecutionGeneration
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item);
    }

    private static bool IsExactRecoverableContinuation(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation)
        => activation is
        {
            Status: GovernedLoopNodeExecutionStatus.Running,
            Descriptor.Kind: GovernedLoopNodeKind.Wait,
            Attempt: { } attempt,
            AttemptOperationId: { } attemptOperationId,
        }
            && run.WaitEvidence.Count(wait => wait is
            {
                ParkEvidence: not null,
                ContinuationEvidence: not null,
            }
                && wait.ActivationOrdinal == activation.ActivationOrdinal
                && string.Equals(wait.NodeId, activation.NodeId, StringComparison.Ordinal)
                && wait.NodeVisitOrdinal == activation.VisitOrdinal
                && string.Equals(wait.CycleId, activation.CycleId, StringComparison.Ordinal)
                && wait.CycleIteration == activation.CycleIteration
                && wait.WaitAttempt == attempt
                && string.Equals(wait.WaitOperationId, attemptOperationId, StringComparison.Ordinal)) == 1;

    private static bool HasTerminalOutcome(
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence activation,
        long startSequence)
        => run.Events.Any(item => item.Sequence > startSequence
            && item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeAttemptFailed
            && item.SequentialNodeEvidence is { } evidence
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && evidence.Attempt == activation.Attempt
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && string.Equals(evidence.CycleId, activation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == activation.CycleIteration);
}
