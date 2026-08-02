namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one durable lifecycle mutation outcome and current proved state.</summary>
/// <param name="Status">The mutation outcome.</param>
/// <param name="State">The current state when safely available.</param>
/// <param name="LifecycleRevision">The current authenticated lifecycle revision when known.</param>
/// <param name="OutcomeAuditPending">Whether terminal audit evidence still requires repair.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
/// <param name="ReplayedOutcome">The persisted terminal outcome when <paramref name="Status"/> indicates an exact replay.</param>
public sealed record CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus Status, CapabilityLifecycleState? State, long? LifecycleRevision, bool OutcomeAuditPending, string Detail, CapabilityLifecycleMutationStatus? ReplayedOutcome = null);
