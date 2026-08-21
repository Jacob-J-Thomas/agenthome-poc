using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Returns a server-selected local provider without exposing its private locator.</summary>
public sealed record CredentialValueProviderResolution(
    CredentialValueProviderResolutionStatus Status,
    CredentialProviderId? ProviderId = null,
    ICredentialValueProvider? Provider = null);
