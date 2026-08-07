using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Verifies issuer authenticity and current authority for the exact credential request.</summary>
public interface ICredentialAuthorityProofVerifier
{
    /// <summary>Authenticates and revalidates the exact request proof against current authority, admitted run identity, and verifier-owned trusted time.</summary>
    /// <remarks>The implementation must observe current UTC from its own trusted clock. The current run identity must come from the admitted runtime context rather than from the proof or caller-supplied request.</remarks>
    ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CredentialContractId currentRunId, CancellationToken cancellationToken);
}
