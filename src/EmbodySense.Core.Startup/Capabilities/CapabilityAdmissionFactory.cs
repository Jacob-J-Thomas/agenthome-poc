using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Composes runtime capability admission over current catalog state projected through the authoritative lifecycle aggregate.</summary>
public static class CapabilityAdmissionFactory
{
    /// <summary>Creates workspace-bound admission that fails closed on recovered or unavailable lifecycle state.</summary>
    public static ICapabilityAdmissionService Create(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        var catalog = new CapabilityCatalogStore(paths, trustProvider);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, trustProvider);
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle);
        return new CapabilityAdmissionService(projection, CapabilityWorkspaceScopeId.Create(paths.RootPath), CapabilityHostRuntime.HostContractVersion, CapabilityHostRuntime.Platform);
    }
}
