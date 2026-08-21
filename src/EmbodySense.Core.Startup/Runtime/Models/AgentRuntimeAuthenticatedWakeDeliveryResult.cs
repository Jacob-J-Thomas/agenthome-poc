namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Reports one authenticated-event wake delivery through the shared governed runtime.</summary>
/// <param name="Status">The closed durable wake outcome.</param>
/// <param name="WakeId">The exact durable wake identity when authenticated evidence exists.</param>
/// <param name="EvidenceHash">The exact durable wake evidence hash when authenticated evidence exists.</param>
/// <param name="ContinuationInvoked">Whether this call invoked the exact idempotent continuation port.</param>
public sealed record AgentRuntimeAuthenticatedWakeDeliveryResult(
    AgentRuntimeAuthenticatedWakeDeliveryStatus Status,
    string? WakeId = null,
    string? EvidenceHash = null,
    bool ContinuationInvoked = false);
