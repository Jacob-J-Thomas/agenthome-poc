namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Requests surface-owned verification of an already-authenticated event delivery.</summary>
/// <param name="CheckpointId">The deterministic sleeping-checkpoint identity.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash.</param>
/// <param name="AuthenticatedEventReference">The exact admitted event subscription.</param>
/// <param name="AuthenticationEvidenceHash">The submitted authentication-evidence hash.</param>
/// <param name="CheckpointPublishedAtUtc">The checkpoint publication time below which events are ineligible.</param>
public sealed record AgentRuntimeAuthenticatedWakeVerificationRequest(
    string CheckpointId,
    string CheckpointHash,
    string AuthenticatedEventReference,
    string AuthenticationEvidenceHash,
    DateTimeOffset CheckpointPublishedAtUtc);
