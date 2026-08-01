using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Verifies issuer authenticity and current authority for the exact credential request.</summary>
public interface ICredentialAuthorityProofVerifier
{
    /// <summary>Authenticates and revalidates the exact request proof against current authority.</summary>
    ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CancellationToken cancellationToken);
}
