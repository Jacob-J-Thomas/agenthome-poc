using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Credentials.Leases;
using EmbodySense.Core.Startup.Credentials.Models;

namespace EmbodySense.Core.Startup.Credentials;

/// <summary>Composes the one canonical local credential broker over persistent lease, registry, and provider seams.</summary>
public static class CredentialBrokerFactory
{
    /// <summary>Creates a broker over explicit trusted local providers without reading or mutating workspace state.</summary>
    public static CredentialBroker Create(
        WorkspacePaths paths,
        FileCapabilityCatalogTrustProvider registryTrustProvider,
        ICredentialProviderLocatorVerifier locatorVerifier,
        ICredentialAuthorityProofVerifier authorityProofVerifier,
        ICredentialLeaseCurrentAuthoritySnapshotSource currentAuthoritySource,
        IReadOnlyList<CredentialValueProviderRegistration> providers,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registryTrustProvider);
        ArgumentNullException.ThrowIfNull(locatorVerifier);
        ArgumentNullException.ThrowIfNull(authorityProofVerifier);
        ArgumentNullException.ThrowIfNull(currentAuthoritySource);
        ArgumentNullException.ThrowIfNull(providers);

        var registry = new CredentialRegistryStore(paths, registryTrustProvider, locatorVerifier, timeProvider);
        var attempts = new CredentialLeaseAttemptStore(paths);
        var currentVerifier = new CredentialLeaseCurrentAuthorityVerifier(currentAuthoritySource);
        var resolver = new ConfiguredCredentialValueProviderResolver(providers);
        var gate = new CredentialLeaseRedemptionGate(registry, attempts, currentVerifier, new CapabilityAuthorityTransaction(paths), timeProvider);
        return new CredentialBroker(authorityProofVerifier, currentVerifier, attempts, gate, registry, registry, resolver, timeProvider);
    }

    /// <summary>Creates the canonical broker with the Windows current-user protected provider as its sole configured value source.</summary>
    public static CredentialBroker CreateWindows(
        WorkspacePaths paths,
        FileCapabilityCatalogTrustProvider registryTrustProvider,
        ICredentialProviderLocatorVerifier locatorVerifier,
        ICredentialAuthorityProofVerifier authorityProofVerifier,
        ICredentialLeaseCurrentAuthoritySnapshotSource currentAuthoritySource,
        TimeProvider? timeProvider = null)
    {
        if (!CredentialProviderId.TryParse("org.embodysense.windows", out var providerId, out _))
        {
            throw new InvalidOperationException("The canonical Windows credential provider identity is invalid.");
        }
        return Create(
            paths,
            registryTrustProvider,
            locatorVerifier,
            authorityProofVerifier,
            currentAuthoritySource,
            [new CredentialValueProviderRegistration(providerId!, new WindowsCredentialValueProvider())],
            timeProvider);
    }
}
