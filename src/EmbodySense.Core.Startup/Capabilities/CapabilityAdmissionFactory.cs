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
        var authority = new CapabilityAuthorityTransaction(paths);
        var catalog = new CapabilityCatalogStore(paths, trustProvider, authorityTransaction: authority);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, trustProvider, authorityTransaction: authority);
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle, authority);
        return new CapabilityAdmissionService(projection, CapabilityWorkspaceScopeId.Create(paths.RootPath), CapabilityHostRuntime.HostContractVersion, CapabilityHostRuntime.Platform, authority);
    }
}
