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
        return Create(paths, trustProvider, authority);
    }

    /// <summary>Creates workspace-bound admission using the supplied shared capability-authority transaction.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="trustProvider">The server-owned catalog and lifecycle trust provider.</param>
    /// <param name="authorityTransaction">The transaction shared with other capability observations and mutations in this runtime.</param>
    /// <returns>Admission that fails closed on recovered or unavailable lifecycle state.</returns>
    public static ICapabilityAdmissionService Create(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, ICapabilityAuthorityTransaction authorityTransaction)
        => Create(paths, trustProvider, authorityTransaction, null);

    /// <summary>Creates workspace-bound admission using a shared authority transaction and trusted clock.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="trustProvider">The server-owned catalog and lifecycle trust provider.</param>
    /// <param name="authorityTransaction">The transaction shared with other authority observations and mutations in this runtime.</param>
    /// <param name="timeProvider">The trusted admission clock, or the system clock when omitted.</param>
    /// <returns>Admission that fails closed on recovered or unavailable lifecycle state.</returns>
    public static ICapabilityAdmissionService Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(authorityTransaction);
        var catalog = new CapabilityCatalogStore(paths, trustProvider, authorityTransaction: authorityTransaction);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, trustProvider, authorityTransaction: authorityTransaction);
        var projection = new CapabilityLifecycleCatalogStore(catalog, lifecycle, authorityTransaction);
        return new CapabilityAdmissionService(
            projection,
            CapabilityWorkspaceScopeId.Create(paths.RootPath),
            CapabilityHostRuntime.HostContractVersion,
            CapabilityHostRuntime.Platform,
            authorityTransaction,
            timeProvider);
    }
}
