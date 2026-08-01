using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Verifies issuer authenticity and current authority for the exact credential request.</summary>
public interface ICredentialAuthorityProofVerifier
{
    /// <summary>Authenticates and revalidates the exact request proof against current authority and verifier-owned trusted time.</summary>
    /// <remarks>The implementation must observe current UTC from its own trusted clock. Request data contains no caller-selected validation timestamp.</remarks>
    ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CancellationToken cancellationToken);
}
