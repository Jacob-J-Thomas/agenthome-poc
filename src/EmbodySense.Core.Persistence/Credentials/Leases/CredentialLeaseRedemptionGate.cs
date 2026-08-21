using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Credentials.Leases;

/// <summary>Orders current restrictive credential lifecycle state against durable single-use redemption boundary entry.</summary>
public sealed class CredentialLeaseRedemptionGate : ICredentialLeaseRedemptionGate
{
    private readonly ICredentialRegistryStore _registry;
    private readonly ICredentialLeaseAttemptStore _attemptStore;
    private readonly ICredentialLeaseCurrentAuthorityVerifier _currentAuthorityVerifier;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the reference-scoped ordering gate over canonical registry and lease stores.</summary>
    /// <param name="registry">The canonical credential registry.</param>
    /// <param name="attemptStore">The durable single-use attempt store.</param>
    /// <param name="currentAuthorityVerifier">The verifier for exact current authority evidence.</param>
    /// <param name="authorityTransaction">The retained workspace authority transaction.</param>
    /// <param name="timeProvider">The optional trusted clock.</param>
    public CredentialLeaseRedemptionGate(
        ICredentialRegistryStore registry,
        ICredentialLeaseAttemptStore attemptStore,
        ICredentialLeaseCurrentAuthorityVerifier currentAuthorityVerifier,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        _currentAuthorityVerifier = currentAuthorityVerifier ?? throw new ArgumentNullException(nameof(currentAuthorityVerifier));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CredentialLeaseBoundaryResult> TryEnterAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken = default)
    {
        if (CredentialLeaseContract.Validate(authorized) is not null
            || authorized.Current.Phase != CredentialLeasePhase.Authorized
            || trustedNowUtc.Offset != TimeSpan.Zero
            || !CredentialReferenceId.TryParse(authorized.Intent.Registry.ReferenceId, out var referenceId, out _))
        {
            return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Corrupt, authorized);
        }

        return await TryEnterOrderedAsync(authorized, lease, referenceId!, trustedNowUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CredentialLeaseBoundaryResult> TryEnterOrderedAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, CredentialReferenceId referenceId, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken)
    {
        CredentialLeaseCurrentVerificationResult? currentAuthority = null;
        var authorityLease = await _authorityTransaction.AcquireValidatedLeaseAsync(async token =>
        {
            currentAuthority = await _currentAuthorityVerifier.VerifyAsync(authorized.Intent, token).ConfigureAwait(false);
            return MatchesCurrent(authorized, currentAuthority);
        }, cancellationToken).ConfigureAwait(false);
        if (authorityLease is null)
        {
            var failure = currentAuthority?.Status == CredentialLeaseCurrentVerificationStatus.Denied ? CredentialFailureCode.Unauthorized : CredentialFailureCode.Unavailable;
            return await CommitNotRedeemedAsync(authorized, lease, trustedNowUtc, failure).ConfigureAwait(false);
        }

        await using (authorityLease.ConfigureAwait(false))
        {
            return TryEnterWithCurrentAuthorityOrdered(authorized, lease, referenceId, trustedNowUtc, cancellationToken);
        }
    }

    private CredentialLeaseBoundaryResult TryEnterWithCurrentAuthorityOrdered(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, CredentialReferenceId referenceId, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken)
    {
        var target = CredentialProviderTarget.Derive(authorized.Intent.Execution.WorkspaceId, referenceId);
        if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
        {
            return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Unavailable, authorized);
        }

        using (operationLock)
        {
            try
            {
                // Named mutex ownership is thread-affine. Block this bounded critical section on the acquiring thread so
                // registry publication and lease-boundary publication remain one cross-process linearization point.
                return TryEnterWithCurrentAuthorityAsync(authorized, lease, trustedNowUtc, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Unavailable, authorized);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Unavailable, authorized);
            }
        }
    }

    private async Task<CredentialLeaseBoundaryResult> TryEnterWithCurrentAuthorityAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken)
    {
        var read = await _registry.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!TryGetTrustedNow(out var currentNowUtc) || currentNowUtc < authorized.Current.RecordedAtUtc)
        {
            return await CommitNotRedeemedAsync(authorized, lease, trustedNowUtc, CredentialFailureCode.Unavailable).ConfigureAwait(false);
        }

        var match = CredentialLeaseRegistryMatcher.Match(authorized.Intent, read, currentNowUtc);
        if (match.Succeeded && TryGetTrustedNow(out var boundaryNowUtc) && boundaryNowUtc >= currentNowUtc)
        {
            currentNowUtc = boundaryNowUtc;
            match = CredentialLeaseRegistryMatcher.Match(authorized.Intent, read, currentNowUtc);
        }
        else if (match.Succeeded)
        {
            return await CommitNotRedeemedAsync(authorized, lease, currentNowUtc, CredentialFailureCode.Unavailable).ConfigureAwait(false);
        }

        if (!match.Succeeded
            || !string.Equals(match.EvidenceHash, authorized.Current.RegistryEvidenceHash, StringComparison.Ordinal)
            || currentNowUtc >= authorized.Intent.EffectiveExpiresAtUtc)
        {
            var failure = currentNowUtc >= authorized.Intent.EffectiveExpiresAtUtc
                ? CredentialFailureCode.Expired
                : match.Failure?.Code ?? CredentialFailureCode.Conflict;
            return await CommitNotRedeemedAsync(authorized, lease, currentNowUtc, failure).ConfigureAwait(false);
        }

        var boundaryVersion = CredentialLeaseContract.Advance(authorized.Intent, authorized.Current, CredentialLeasePhase.RedemptionBoundaryReached, currentNowUtc);
        var boundary = CredentialLeaseContract.CreateHistory(authorized.Intent, [.. authorized.Versions, boundaryVersion]);
        var commit = await _attemptStore.CompareExchangeAsync(authorized.Current.ContentHash, boundary, lease, CancellationToken.None).ConfigureAwait(false);
        return commit.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed
            ? new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Entered, commit.History)
            : new CredentialLeaseBoundaryResult(Map(commit.Status), commit.History ?? authorized);
    }

    private static bool MatchesCurrent(CredentialLeaseAttemptHistory authorized, CredentialLeaseCurrentVerificationResult? current)
        => current is not null
            && current.Status == CredentialLeaseCurrentVerificationStatus.Authorized
            && string.Equals(current.VerifiedIntentHash, authorized.Intent.ContentHash, StringComparison.Ordinal)
            && string.Equals(current.EvidenceHash, authorized.Current.CurrentAuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.CurrentAuthorityDecisionHash, authorized.Intent.Authority.CurrentAuthorityDecisionHash, StringComparison.Ordinal)
            && string.Equals(current.CapabilityDescriptorHash, authorized.Intent.Capability.CapabilityDescriptorHash, StringComparison.Ordinal)
            && (authorized.Intent.Profile.Applicability == CredentialLeaseProfileApplicability.NotApplicable && current.ProfileHash is null
                || authorized.Intent.Profile.Applicability == CredentialLeaseProfileApplicability.Applicable && string.Equals(current.ProfileHash, authorized.Intent.Profile.ProfileHash, StringComparison.Ordinal));

    private async Task<CredentialLeaseBoundaryResult> CommitNotRedeemedAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, DateTimeOffset trustedNowUtc, CredentialFailureCode failure)
    {
        var recordedAtUtc = trustedNowUtc < authorized.Current.RecordedAtUtc ? authorized.Current.RecordedAtUtc : trustedNowUtc;
        var deniedVersion = CredentialLeaseContract.Advance(authorized.Intent, authorized.Current, CredentialLeasePhase.NotRedeemed, recordedAtUtc, failureCode: failure);
        var denied = CredentialLeaseContract.CreateHistory(authorized.Intent, [.. authorized.Versions, deniedVersion]);
        var deniedCommit = await _attemptStore.CompareExchangeAsync(authorized.Current.ContentHash, denied, lease, CancellationToken.None).ConfigureAwait(false);
        return deniedCommit.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed
            ? new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.NotRedeemed, deniedCommit.History)
            : new CredentialLeaseBoundaryResult(Map(deniedCommit.Status), deniedCommit.History ?? authorized);
    }

    private bool TryGetTrustedNow(out DateTimeOffset trustedNowUtc)
    {
        try
        {
            trustedNowUtc = _timeProvider.GetUtcNow();
            return trustedNowUtc != default && trustedNowUtc.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            trustedNowUtc = default;
            return false;
        }
    }

    private static CredentialLeaseBoundaryStatus Map(CredentialLeaseAttemptStoreStatus status) => status switch
    {
        CredentialLeaseAttemptStoreStatus.Conflict or CredentialLeaseAttemptStoreStatus.OperationInProgress => CredentialLeaseBoundaryStatus.Conflict,
        CredentialLeaseAttemptStoreStatus.Corrupt => CredentialLeaseBoundaryStatus.Corrupt,
        _ => CredentialLeaseBoundaryStatus.Unavailable,
    };
}
