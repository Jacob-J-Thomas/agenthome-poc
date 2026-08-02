namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one structured artifact intake outcome.</summary>
/// <param name="Status">The intake status.</param>
/// <param name="OperationId">The operation identity.</param>
/// <param name="Activation">The current or resulting activation when known.</param>
/// <param name="Trust">The server-owned trust decision when reached.</param>
/// <param name="Detail">A bounded redacted explanation.</param>
public sealed record CapabilityArtifactIntakeResult(CapabilityArtifactIntakeStatus Status, string OperationId, CapabilityArtifactActivation? Activation, CapabilityArtifactTrustDecision? Trust, string Detail);
