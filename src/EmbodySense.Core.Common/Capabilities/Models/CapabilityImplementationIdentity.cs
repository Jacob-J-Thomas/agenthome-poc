namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Identifies the provider-owned implementation behind a capability descriptor.
/// </summary>
/// <param name="ProviderId">The provider namespace.</param>
/// <param name="ImplementationId">The provider-owned canonical implementation path.</param>
public sealed record CapabilityImplementationIdentity(CapabilityProviderId ProviderId, string ImplementationId);
