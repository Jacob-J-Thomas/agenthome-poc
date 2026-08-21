namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one exact timestamp or already-authenticated event wake.</summary>
/// <param name="CheckpointId">The deterministic checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticationEvidenceHash">The event-authentication evidence hash, required only for authenticated-event wakes.</param>
public sealed record GovernedLoopWakeRequest(
    string CheckpointId,
    string CheckpointHash,
    string? AuthenticationEvidenceHash = null);
