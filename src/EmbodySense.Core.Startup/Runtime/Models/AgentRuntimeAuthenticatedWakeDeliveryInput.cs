namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Delivers one surface-authenticated event to an exact governed Wait checkpoint.</summary>
/// <param name="CheckpointId">The deterministic checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticationEvidenceHash">The surface-owned event-authentication evidence hash.</param>
public sealed record AgentRuntimeAuthenticatedWakeDeliveryInput(
    string CheckpointId,
    string CheckpointHash,
    string AuthenticationEvidenceHash);
