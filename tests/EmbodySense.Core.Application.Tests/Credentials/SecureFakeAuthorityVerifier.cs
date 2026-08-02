using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class SecureFakeAuthorityVerifier(byte[] signingKey, TimeProvider timeProvider) : ICredentialAuthorityProofVerifier
{
    private readonly byte[] _signingKey = signingKey.ToArray();
    private readonly TimeProvider _timeProvider = timeProvider;

    public ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CredentialContractId currentRunId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !CredentialContractValidator.Validate(request, currentRunId, _timeProvider.GetUtcNow()).IsValid)
        {
            return ValueTask.FromResult(CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized)));
        }

        var expected = Sign(request.AuthorityProof, _signingKey);
        var result = expected.FixedTimeEquals(request.AuthorityProof.Authenticator) ? CredentialAuthorityVerificationResult.Accept() : CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized));
        return ValueTask.FromResult(result);
    }

    internal static CredentialContractHash Sign(CredentialAuthorityProof proof, byte[] signingKey)
    {
        if (!CredentialContractJson.TrySerializeAuthorityClaim(proof, out var claim, out _))
        {
            throw new InvalidOperationException("The test authority claim must be serializable.");
        }

        var authenticator = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(claim!));
        if (!CredentialContractHash.TryParse("sha256:" + Convert.ToHexStringLower(authenticator), out var parsed, out _))
        {
            throw new InvalidOperationException("The test authenticator must be valid.");
        }

        return parsed!;
    }
}
