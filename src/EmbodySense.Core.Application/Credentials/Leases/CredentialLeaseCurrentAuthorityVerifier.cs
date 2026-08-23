using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Requires complete exact agreement with a fresh canonical credential-authority snapshot.</summary>
public sealed class CredentialLeaseCurrentAuthorityVerifier(ICredentialLeaseCurrentAuthoritySnapshotSource source) : ICredentialLeaseCurrentAuthorityVerifier
{
    private readonly ICredentialLeaseCurrentAuthoritySnapshotSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public async Task<CredentialLeaseCurrentVerificationResult> VerifyAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken = default)
    {
        if (CredentialLeaseContract.Validate(intent) is not null)
        {
            return new CredentialLeaseCurrentVerificationResult(CredentialLeaseCurrentVerificationStatus.Denied);
        }

        CredentialLeaseCurrentAuthoritySnapshot snapshot;
        try
        {
            snapshot = await _source.ReadAsync(intent.CredentialUseOperationId, intent.CredentialUseGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CredentialLeaseCurrentVerificationResult(CredentialLeaseCurrentVerificationStatus.Unavailable);
        }

        if (snapshot is null || snapshot.Status != CredentialLeaseCurrentVerificationStatus.Authorized)
        {
            return new CredentialLeaseCurrentVerificationResult(snapshot?.Status == CredentialLeaseCurrentVerificationStatus.Denied
                ? CredentialLeaseCurrentVerificationStatus.Denied
                : CredentialLeaseCurrentVerificationStatus.Unavailable);
        }
        if (snapshot.Intent is null
            || CredentialLeaseContract.Validate(snapshot.Intent) is not null
            || !IsHash(snapshot.EvidenceHash))
        {
            return new CredentialLeaseCurrentVerificationResult(CredentialLeaseCurrentVerificationStatus.Unavailable);
        }
        if (!FixedTimeEquals(intent.ContentHash, snapshot.Intent.ContentHash))
        {
            return new CredentialLeaseCurrentVerificationResult(CredentialLeaseCurrentVerificationStatus.Denied);
        }

        return new CredentialLeaseCurrentVerificationResult(
            CredentialLeaseCurrentVerificationStatus.Authorized,
            snapshot.Intent.Authority.CurrentAuthorityDecisionHash,
            snapshot.Intent.Capability.CapabilityDescriptorHash,
            snapshot.Intent.Profile.ProfileHash,
            snapshot.Intent.ContentHash,
            snapshot.EvidenceHash);
    }

    private static bool FixedTimeEquals(string left, string right)
        => left.Length == right.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static bool IsHash(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
