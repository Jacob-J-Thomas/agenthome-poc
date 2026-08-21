using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Binds one idempotent continuation to its exact checkpoint, wake, and optimistic current posture.</summary>
/// <param name="Checkpoint">The immutable sleeping checkpoint.</param>
/// <param name="Identity">The deterministic wake identity.</param>
/// <param name="ContinuationOperationId">The stable idempotency identity retained before invocation.</param>
/// <param name="ExpectedPostureHash">The exact current-posture fence admitted before invocation, or <see langword="null"/> for read-only reconciliation.</param>
public sealed record GovernedLoopWakeContinuationRequest(
    GovernedLoopSleepCheckpoint Checkpoint,
    GovernedLoopWakeIdentity Identity,
    string ContinuationOperationId,
    string? ExpectedPostureHash);
