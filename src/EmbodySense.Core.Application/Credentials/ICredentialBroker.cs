using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Authorizes and resolves one credential only through a trusted callback.</summary>
public interface ICredentialBroker
{
    /// <summary>Uses a credential only after exact request validation and trusted authority verification.</summary>
    /// <param name="request">The untrusted credential use request and signed authority proof.</param>
    /// <param name="currentRunId">The independently sourced identity of the runtime invocation admitted by the harness.</param>
    /// <param name="trustedConsumer">The synchronous trusted consumer for provider-owned ephemeral bytes.</param>
    /// <param name="cancellationToken">A token that may cancel the operation before credential use begins.</param>
    ValueTask<CredentialUseResult> UseAsync(CredentialUseRequest request, CredentialContractId currentRunId, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken);
}
