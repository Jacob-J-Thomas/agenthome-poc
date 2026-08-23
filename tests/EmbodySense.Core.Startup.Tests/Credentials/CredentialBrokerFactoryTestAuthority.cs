using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Startup.Tests.Credentials;

internal sealed class CredentialBrokerFactoryTestAuthority : ICredentialAuthorityProofVerifier, ICredentialLeaseCurrentAuthoritySnapshotSource
{
    public ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CredentialContractId currentRunId, CancellationToken cancellationToken)
        => ValueTask.FromResult(CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized)));

    public Task<CredentialLeaseCurrentAuthoritySnapshot> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
        => Task.FromResult(new CredentialLeaseCurrentAuthoritySnapshot(CredentialLeaseCurrentVerificationStatus.Unavailable));
}
