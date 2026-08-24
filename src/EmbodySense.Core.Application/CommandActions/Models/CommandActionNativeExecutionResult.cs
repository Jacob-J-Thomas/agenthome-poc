namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Returns one closed native command execution result without adapter-authored diagnostics.</summary>
/// <param name="Status">The native dispatch posture.</param>
/// <param name="Outcome">The conclusive outcome only when observed.</param>
/// <param name="DispatchNotStartedReason">The closed pre-dispatch reason only when dispatch did not start.</param>
public sealed record CommandActionNativeExecutionResult(
    CommandActionNativeExecutionStatus Status,
    CommandActionNativeOutcome? Outcome,
    CommandActionDispatchNotStartedReason? DispatchNotStartedReason = null);
