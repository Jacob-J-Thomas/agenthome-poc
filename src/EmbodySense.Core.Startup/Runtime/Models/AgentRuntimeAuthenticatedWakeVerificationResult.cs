namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Reports one surface-owned authenticated-event verification attempt.</summary>
/// <param name="Status">The closed verification status.</param>
/// <param name="Verification">The exact evidence, present only for a verified result.</param>
public sealed record AgentRuntimeAuthenticatedWakeVerificationResult(
    AgentRuntimeAuthenticatedWakeVerificationStatus Status,
    AgentRuntimeAuthenticatedWakeVerification? Verification = null);
