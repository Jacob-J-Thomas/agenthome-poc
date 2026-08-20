namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Projects inspectable queue, ownership, and dispatch posture through Core.Startup.</summary>
/// <param name="DeliveryId">The delivery identity.</param>
/// <param name="LoopId">The loop identity.</param>
/// <param name="State">The durable queue state.</param>
/// <param name="Revision">The exact entry revision.</param>
/// <param name="WorkerId">The current or last worker identity.</param>
/// <param name="LeaseGeneration">The ownership generation.</param>
/// <param name="LeaseExpiresAtUtc">The ownership expiry instant.</param>
/// <param name="LeaseReleasedAtUtc">The explicit release instant.</param>
/// <param name="DispatchOutcome">The durable dispatch posture.</param>
/// <param name="DispatchOperationId">The idempotent runner operation identity.</param>
/// <param name="DispatchDetail">The bounded outcome detail.</param>
/// <param name="GovernedRunId">The exact governed run identity for a proved accepted or terminal outcome.</param>
/// <param name="GovernedAdmissionRequestHash">The exact governed admission request hash.</param>
/// <param name="GovernedLoopReferenceHash">The exact domain-separated closed target-reference hash.</param>
public sealed record TriggerWorkerEntrySnapshot(string DeliveryId, string LoopId, string State, long Revision, string? WorkerId, long? LeaseGeneration, DateTimeOffset? LeaseExpiresAtUtc, DateTimeOffset? LeaseReleasedAtUtc, string? DispatchOutcome, string? DispatchOperationId, string? DispatchDetail, string? GovernedRunId, string? GovernedAdmissionRequestHash, string? GovernedLoopReferenceHash);
