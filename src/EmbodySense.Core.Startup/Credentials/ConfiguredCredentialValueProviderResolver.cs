using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Startup.Credentials.Models;

namespace EmbodySense.Core.Startup.Credentials;

/// <summary>Resolves only the exact finite set of local providers selected by server composition.</summary>
public sealed class ConfiguredCredentialValueProviderResolver : ICredentialValueProviderResolver
{
    private readonly IReadOnlyDictionary<string, CredentialValueProviderRegistration> _providers;

    /// <summary>Creates an immutable provider map and rejects null or duplicate registrations.</summary>
    public ConfiguredCredentialValueProviderResolver(IEnumerable<CredentialValueProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var configured = new Dictionary<string, CredentialValueProviderRegistration>(StringComparer.Ordinal);
        foreach (var registration in providers)
        {
            if (registration?.ProviderId is null || registration.Provider is null
                || !configured.TryAdd(registration.ProviderId.Value, registration))
            {
                throw new ArgumentException("Credential provider registrations must be non-null and uniquely identified.", nameof(providers));
            }
        }
        if (configured.Count is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(providers), "Configure between one and sixteen trusted local credential providers.");
        }
        _providers = configured;
    }

    /// <inheritdoc />
    public Task<CredentialValueProviderResolution> ResolveAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<CredentialValueProviderResolution>(cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(workspaceId) || referenceId is null || providerId is null)
        {
            return Task.FromResult(new CredentialValueProviderResolution(CredentialValueProviderResolutionStatus.Unavailable));
        }

        return Task.FromResult(_providers.TryGetValue(providerId.Value, out var registration)
            ? new CredentialValueProviderResolution(CredentialValueProviderResolutionStatus.Resolved, registration.ProviderId, registration.Provider)
            : new CredentialValueProviderResolution(CredentialValueProviderResolutionStatus.NotConfigured));
    }
}
