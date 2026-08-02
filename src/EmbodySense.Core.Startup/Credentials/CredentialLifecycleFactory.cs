using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.Core.Startup.Credentials;

/// <summary>Composes the credential lifecycle boundary over one persistence-owned closed registry projection.</summary>
public static class CredentialLifecycleFactory
{
    /// <summary>Creates the lifecycle service without reading or mutating workspace state.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="registryTrustProvider">The server-owned registry trust provider.</param>
    /// <param name="locatorVerifier">The provider-owned locator verifier.</param>
    /// <param name="provider">The credential value provider.</param>
    /// <param name="locatorSource">The provider-owned source of newly written opaque locators.</param>
    /// <param name="dependentIndex">The complete capability dependent index.</param>
    /// <param name="activeRunIndex">The authoritative active-run index.</param>
    /// <param name="auditLog">The append-only workspace audit log.</param>
    /// <param name="timeProvider">The optional server clock used for durable registry timestamps.</param>
    /// <returns>The fully composed credential lifecycle service.</returns>
    /// <remarks>The factory authenticates lifecycle actors against the current process user and delegates to persistence-owned composition that derives reconciliation from authenticated confirmed requests and exact durable interrupted-intent state. No caller-supplied authentication or reconciliation authority is accepted or returned.</remarks>
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
        return CredentialLifecyclePersistenceFactory.Create(paths, registryTrustProvider, locatorVerifier, provider, locatorSource, dependentIndex, activeRunIndex, auditLog, timeProvider);
    }
}
