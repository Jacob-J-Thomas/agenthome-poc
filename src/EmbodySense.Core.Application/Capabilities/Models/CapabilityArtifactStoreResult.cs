namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one structured durable artifact-store outcome.</summary>
/// <param name="Status">The store status.</param>
/// <param name="Activation">The current or resulting activation when known.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record CapabilityArtifactStoreResult(CapabilityArtifactStoreStatus Status, CapabilityArtifactActivation? Activation, string Detail);
