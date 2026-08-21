using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Loops.Sleep;

internal static class GovernedLoopSleepPosturePolicy
{
    internal static bool IsWellFormed(
        GovernedLoopSleepCurrentPosture? posture,
        GovernedLoopExecutionBinding expectedBinding,
        DateTimeOffset readStartedAtUtc,
        DateTimeOffset readCompletedAtUtc)
    {
        if (posture?.Execution is null
            || posture.Publication is null
            || !GovernedLoopExecutionValidator.Validate(posture.Execution).IsValid
            || !GovernedLoopRevisionContractValidator.Validate(posture.Publication).IsValid
            || !Equals(posture.Execution.Lifecycle.Binding, posture.Execution.Frontier.Binding)
            || !string.Equals(posture.Execution.Lifecycle.Binding.RunId, expectedBinding.RunId, StringComparison.Ordinal)
            || posture.Publication.Revision != posture.Execution.Lifecycle.Binding.Revision
            || !IsHash(posture.UnattendedAuthorityEvidenceHash)
            || !IsHash(posture.PostureHash)
            || !IsUtc(posture.ObservedAtUtc)
            || posture.ObservedAtUtc < readStartedAtUtc
            || posture.ObservedAtUtc > readCompletedAtUtc
            || posture.ObservedAtUtc < posture.Execution.Lifecycle.Payload.UpdatedAtUtc
            || posture.ObservedAtUtc < posture.Execution.Frontier.Payload.UpdatedAtUtc
            || posture.Execution.Effects.Any(effect => posture.ObservedAtUtc < effect.Payload.UpdatedAtUtc)
            || posture.Execution.Projections.Any(projection => posture.ObservedAtUtc < projection.Payload.UpdatedAtUtc)
            || posture.ExecutionExpiresAtUtc is { } expiresAtUtc
                && (!IsUtc(expiresAtUtc) || expiresAtUtc < posture.Execution.Lifecycle.Payload.CreatedAtUtc))
        {
            return false;
        }

        return true;
    }

    internal static GovernedLoopSleepPostureDecision EvaluatePublication(
        GovernedLoopSleepCurrentPosture posture,
        GovernedLoopSleepCheckpoint checkpoint,
        DateTimeOffset evaluatedAtUtc)
        => Evaluate(posture, checkpoint, evaluatedAtUtc, requireExactFrontier: true);

    internal static GovernedLoopSleepPostureDecision EvaluateWake(
        GovernedLoopSleepCurrentPosture posture,
        GovernedLoopSleepCheckpoint checkpoint,
        DateTimeOffset evaluatedAtUtc)
        => Evaluate(posture, checkpoint, evaluatedAtUtc, requireExactFrontier: false);

    private static GovernedLoopSleepPostureDecision Evaluate(
        GovernedLoopSleepCurrentPosture posture,
        GovernedLoopSleepCheckpoint checkpoint,
        DateTimeOffset evaluatedAtUtc,
        bool requireExactFrontier)
    {
        var execution = posture.Execution;
        var lifecycle = execution.Lifecycle.Payload;
        var frontier = execution.Frontier.Payload;
        if (!Equals(execution.Lifecycle.Binding, checkpoint.Binding.Execution)
            || !Equals(posture.Publication, checkpoint.Binding.Publication))
        {
            return GovernedLoopSleepPostureDecision.Stale;
        }

        if (lifecycle.Status is GovernedLoopRunStatus.CancelRequested or GovernedLoopRunStatus.Cancelled
            || frontier.Status == GovernedLoopFrontierStatus.Cancelled)
        {
            return GovernedLoopSleepPostureDecision.Cancelled;
        }

        if (posture.ExecutionExpiresAtUtc is { } expiresAtUtc && evaluatedAtUtc >= expiresAtUtc
            || lifecycle.Status is GovernedLoopRunStatus.Completed or GovernedLoopRunStatus.Failed)
        {
            return GovernedLoopSleepPostureDecision.Expired;
        }

        if (lifecycle.Status is GovernedLoopRunStatus.PauseRequested or GovernedLoopRunStatus.Paused)
        {
            return GovernedLoopSleepPostureDecision.Paused;
        }

        if (lifecycle.Status == GovernedLoopRunStatus.NeedsReview
            || frontier.Status == GovernedLoopFrontierStatus.ReviewBlocked
            || !posture.UnattendedExecutionPermitted)
        {
            return GovernedLoopSleepPostureDecision.ReviewBlocked;
        }

        if (requireExactFrontier
            && (frontier.FrontierVersion != checkpoint.Binding.FrontierVersion
                || !string.Equals(frontier.ContentHash, checkpoint.Binding.FrontierHash, StringComparison.Ordinal)))
        {
            return GovernedLoopSleepPostureDecision.Stale;
        }

        var node = frontier.Nodes.FirstOrDefault(candidate => candidate.ActivationOrdinal == checkpoint.Binding.ActivationOrdinal);
        if (node is null
            || !string.Equals(node.NodeId, checkpoint.Binding.NodeId, StringComparison.Ordinal)
            || node.VisitOrdinal != checkpoint.Binding.NodeVisitOrdinal
            || !string.Equals(node.CycleId, checkpoint.Binding.CycleId, StringComparison.Ordinal)
            || node.CycleIteration != checkpoint.Binding.CycleIteration
            || node.Attempt != checkpoint.Binding.WaitAttempt
            || !string.Equals(node.AttemptOperationId, checkpoint.Binding.WaitOperationId, StringComparison.Ordinal))
        {
            return GovernedLoopSleepPostureDecision.Stale;
        }

        if (node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked)
        {
            return GovernedLoopSleepPostureDecision.ReviewBlocked;
        }

        if (execution.Effects.Any(IsOpenOrAmbiguous))
        {
            return GovernedLoopSleepPostureDecision.AmbiguousAttempt;
        }

        var aggregateEligible = lifecycle.Status == GovernedLoopRunStatus.Waiting
                && frontier.Status == GovernedLoopFrontierStatus.Waiting
            || lifecycle.Status == GovernedLoopRunStatus.Running
                && frontier.Status == GovernedLoopFrontierStatus.Active;
        return aggregateEligible && node.Status == GovernedLoopNodeExecutionStatus.Waiting
            ? GovernedLoopSleepPostureDecision.Eligible
            : GovernedLoopSleepPostureDecision.Stale;
    }

    private static bool IsOpenOrAmbiguous(GovernedLoopEffectPosture effect)
        => effect.Payload.Phase is not (GovernedLoopEffectPhase.DispatchNotStarted
            or GovernedLoopEffectPhase.Committed
            or GovernedLoopEffectPhase.Reconciled);

    internal static bool IsHash(string? value)
        => value?.Length == GovernedLoopExecutionLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
