namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Captures surface-owned verification of one exact authenticated-event delivery.</summary>
/// <param name="CheckpointId">The deterministic sleeping-checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticatedEventReference">The exact admitted event subscription.</param>
/// <param name="AuthenticationEvidenceHash">The exact verified authentication-evidence hash.</param>
/// <param name="OccurredAtUtc">The trusted UTC time at which the event occurred.</param>
/// <param name="AuthenticatedAtUtc">The trusted UTC time at which the delivery was authenticated.</param>
/// <param name="Eligible">Whether the authoritative source admits the event for this checkpoint.</param>
public sealed record AgentRuntimeAuthenticatedWakeVerification(
    string CheckpointId,
    string CheckpointHash,
    string AuthenticatedEventReference,
    string AuthenticationEvidenceHash,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset AuthenticatedAtUtc,
    bool Eligible);
