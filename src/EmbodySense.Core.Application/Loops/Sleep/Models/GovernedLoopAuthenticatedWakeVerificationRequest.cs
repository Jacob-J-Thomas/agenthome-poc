namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests trusted verification of one already-authenticated event delivery.</summary>
/// <param name="CheckpointId">The deterministic sleeping-checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticatedEventReference">The exact event subscription admitted by the checkpoint.</param>
/// <param name="AuthenticationEvidenceHash">The submitted authentication-evidence hash to verify.</param>
/// <param name="CheckpointPublishedAtUtc">The UTC time at which the checkpoint became eligible to observe events.</param>
public sealed record GovernedLoopAuthenticatedWakeVerificationRequest(
    string CheckpointId,
    string CheckpointHash,
    string AuthenticatedEventReference,
    string AuthenticationEvidenceHash,
    DateTimeOffset CheckpointPublishedAtUtc);
