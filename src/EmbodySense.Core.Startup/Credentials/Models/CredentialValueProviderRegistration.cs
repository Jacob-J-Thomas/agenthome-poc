using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Startup.Credentials.Models;

/// <summary>Registers one explicitly trusted local credential provider under its immutable server-owned identity.</summary>
public sealed record CredentialValueProviderRegistration(CredentialProviderId ProviderId, ICredentialValueProvider Provider);
