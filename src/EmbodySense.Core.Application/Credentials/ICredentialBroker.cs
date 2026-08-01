using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Authorizes and resolves one credential only through a trusted callback.</summary>
public interface ICredentialBroker
{
    /// <summary>Uses a credential only after exact request validation and trusted authority verification.</summary>
    ValueTask<CredentialUseResult> UseAsync(CredentialUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken);
}
