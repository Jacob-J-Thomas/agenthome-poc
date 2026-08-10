namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns one safe capability lifecycle confirmation outcome.</summary>
/// <param name="Status">The stable outcome token.</param>
/// <param name="IsCommitted">Whether the effective terminal operation applied a mutation.</param>
/// <param name="ReplayedOutcome">The prior terminal outcome when this response is an exact replay.</param>
/// <param name="State">The current safe lifecycle state when proved.</param>
/// <param name="LifecycleRevision">The current exact lifecycle revision when known.</param>
/// <param name="OutcomeAuditPending">Whether terminal audit repair remains pending.</param>
/// <param name="Detail">The bounded operator-facing explanation.</param>
public sealed record CapabilityLifecycleMutationResponse(string Status, bool IsCommitted, string? ReplayedOutcome, CapabilityLifecycleMutationStateSnapshot? State, long? LifecycleRevision, bool OutcomeAuditPending, string Detail);
