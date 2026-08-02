using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Creates the persistent credential lifecycle aggregate without exposing its lifecycle commit or actor-authentication boundary.</summary>
public static class CredentialLifecyclePersistenceFactory
{
    /// <summary>Creates one lifecycle service whose private registry projection binds the current process user to current durable lifecycle evidence.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="registryTrustProvider">The server-owned registry trust provider.</param>
    /// <param name="locatorVerifier">The provider-owned locator verifier.</param>
    /// <param name="provider">The credential value provider.</param>
    /// <param name="locatorSource">The provider-owned source of newly written opaque locators.</param>
    /// <param name="dependentIndex">The complete capability dependent index.</param>
    /// <param name="activeRunIndex">The authoritative active-run index.</param>
    /// <param name="auditLog">The append-only workspace audit log.</param>
    /// <param name="timeProvider">The optional server clock shared by registry persistence.</param>
    /// <returns>The fully composed credential lifecycle service.</returns>
    public static CredentialLifecycleService Create(
        WorkspacePaths paths,
        FileCapabilityCatalogTrustProvider registryTrustProvider,
        ICredentialProviderLocatorVerifier locatorVerifier,
        ICredentialValueProvider provider,
        ICredentialProviderLocatorSource locatorSource,
        ICapabilityDependentIndex dependentIndex,
        ICredentialActiveRunIndex activeRunIndex,
        IAuditLog auditLog,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registryTrustProvider);
        ArgumentNullException.ThrowIfNull(locatorVerifier);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(locatorSource);
        ArgumentNullException.ThrowIfNull(dependentIndex);
        ArgumentNullException.ThrowIfNull(activeRunIndex);
        ArgumentNullException.ThrowIfNull(auditLog);
        var registry = new CredentialRegistryStore(paths, registryTrustProvider, locatorVerifier, timeProvider);
        var lifecycleRegistry = new CredentialLifecycleRegistryStore(registry);
        return new CredentialLifecycleService(lifecycleRegistry, provider, locatorSource, dependentIndex, activeRunIndex, auditLog, new CapabilityAuthorityTransaction(paths));
    }
}
