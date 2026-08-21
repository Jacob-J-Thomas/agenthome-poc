namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Captures trusted verification of one exact authenticated-event delivery.</summary>
/// <param name="CheckpointId">The deterministic sleeping-checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticatedEventReference">The exact event subscription admitted by the checkpoint.</param>
/// <param name="AuthenticationEvidenceHash">The exact verified authentication-evidence hash.</param>
/// <param name="OccurredAtUtc">The trusted UTC time at which the event occurred.</param>
/// <param name="AuthenticatedAtUtc">The trusted UTC time at which the delivery was authenticated.</param>
/// <param name="Eligible">Whether the authoritative source admits this event for the exact checkpoint.</param>
public sealed record GovernedLoopAuthenticatedWakeVerification(
    string CheckpointId,
    string CheckpointHash,
    string AuthenticatedEventReference,
    string AuthenticationEvidenceHash,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset AuthenticatedAtUtc,
    bool Eligible);
