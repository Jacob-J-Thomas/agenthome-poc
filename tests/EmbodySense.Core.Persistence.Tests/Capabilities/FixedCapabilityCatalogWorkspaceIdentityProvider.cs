using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class FixedCapabilityCatalogWorkspaceIdentityProvider(string identity) : ICapabilityCatalogWorkspaceIdentityProvider
{
    public string Create(string physicalIdentityMaterial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIdentityMaterial);
        return identity;
    }
}
