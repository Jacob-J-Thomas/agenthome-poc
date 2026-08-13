namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests restart reconciliation of one prepared or ambiguous wake operation.</summary>
/// <param name="CheckpointId">The exact checkpoint identity.</param>
/// <param name="WakeId">The deterministic wake identity.</param>
public sealed record GovernedLoopWakeReconciliationRequest(string CheckpointId, string WakeId);
