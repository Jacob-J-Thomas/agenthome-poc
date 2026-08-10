using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Revalidates one exact immutable grant against current dependency and trusted-time posture.</summary>
public sealed class AuthorityGrantResolver : IAuthorityGrantResolver
{
    private readonly IAuthorityGrantStore _store;
    private readonly AuthorityGrantDependencyEvaluator _dependencies;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an exact fail-closed grant resolver.</summary>
    public AuthorityGrantResolver(
        IAuthorityGrantStore store,
        IAuthorityGrantProfileSource profileSource,
        IAuthorityGrantRoleSource roleSource,
        IGovernedLoopPublishedRevisionSource publishedLoopSource,
        IGovernedLoopGrantBindingSource loopBindingSource,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dependencies = new AuthorityGrantDependencyEvaluator(profileSource, roleSource, publishedLoopSource, loopBindingSource);
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        AuthorityGrantResolution? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completed = await ResolveUnderFenceAsync(reference, transactionToken).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completed is null)
        {
            throw;
        }
        catch (Exception)
        {
            return HasExactActiveProof(completed)
                ? completed!
                : Result(completed is null ? AuthorityGrantResolutionStatus.Unavailable : AuthorityGrantResolutionStatus.Ambiguous, SafeReference(reference));
        }
    }

    private async Task<AuthorityGrantResolution> ResolveUnderFenceAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken)
    {
        if (!IsValidReference(reference))
        {
            return Result(AuthorityGrantResolutionStatus.Invalid, null);
        }

        AuthorityGrantStoreReadResult read;
        try
        {
            read = await _store.ReadAsync(reference!.GrantId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantResolutionStatus.Unavailable, reference);
        }

        if (read is null || read.StoreGeneration < 0 || !Enum.IsDefined(read.Status) || read.Status == AuthorityGrantStoreReadStatus.Unknown)
        {
            return Result(AuthorityGrantResolutionStatus.Ambiguous, reference);
        }

        if (read.Status == AuthorityGrantStoreReadStatus.NotFound && read.Snapshot is null && read.ExistingOperation is null)
        {
            return Result(AuthorityGrantResolutionStatus.NotFound, reference);
        }

        if (read.Status == AuthorityGrantStoreReadStatus.Unavailable && read.Snapshot is null && read.ExistingOperation is null)
        {
            return Result(AuthorityGrantResolutionStatus.Unavailable, reference);
        }

        if (read.Status != AuthorityGrantStoreReadStatus.Ready
            || read.ExistingOperation is not null
            || !AuthorityGrantStoreSnapshotGuard.TryCapture(read.Snapshot, reference!.GrantId, read.StoreGeneration, out var snapshot))
        {
            return Result(AuthorityGrantResolutionStatus.Ambiguous, reference);
        }

        var grant = AuthorityGrantStoreSnapshotGuard.Find(snapshot!, reference);
        if (grant is null)
        {
            return Result(AuthorityGrantResolutionStatus.NotFound, reference);
        }

        if (!AuthorityGrantStoreSnapshotGuard.Matches(reference, snapshot!.CurrentGrant))
        {
            return Result(AuthorityGrantResolutionStatus.Stale, reference, grant);
        }

        var now = UtcNow();
        if (now == default || now < grant.RecordedAtUtc)
        {
            return Result(AuthorityGrantResolutionStatus.Unavailable, reference, grant);
        }

        if (grant.Status == AuthorityGrantLifecycleStatus.Suspended)
        {
            return Result(AuthorityGrantResolutionStatus.Suspended, reference, grant, now);
        }

        if (grant.Status == AuthorityGrantLifecycleStatus.Revoked)
        {
            return Result(AuthorityGrantResolutionStatus.Revoked, reference, grant, now);
        }

        if (grant.Status == AuthorityGrantLifecycleStatus.Expired || grant.Boundary.ExpiresAtUtc is { } expiry && expiry <= now)
        {
            return Result(AuthorityGrantResolutionStatus.Expired, reference, grant, now);
        }

        if (grant.Status != AuthorityGrantLifecycleStatus.Active)
        {
            return Result(AuthorityGrantResolutionStatus.Ambiguous, reference, grant, now);
        }

        if (now < grant.Boundary.EffectiveAtUtc)
        {
            return Result(AuthorityGrantResolutionStatus.NotEffective, reference, grant, now);
        }

        (AuthorityGrantOperationFailureCode FailureCode, string EvidenceHash) dependencies;
        try
        {
            dependencies = await _dependencies.EvaluateAsync(grant.Binding, grant.RequestedCeiling, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (dependencies.FailureCode != AuthorityGrantOperationFailureCode.None)
        {
            return Result(Map(dependencies.FailureCode), reference, grant, now);
        }

        return new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Active, reference, grant, grant.RequestedCeiling, dependencies.EvidenceHash, now);
    }

    private DateTimeOffset UtcNow()
    {
        try
        {
            var value = _timeProvider.GetUtcNow();
            return value == default || value.Offset != TimeSpan.Zero ? default : value;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static AuthorityGrantResolutionStatus Map(AuthorityGrantOperationFailureCode failureCode) => failureCode switch
    {
        AuthorityGrantOperationFailureCode.ProfileUnavailable => AuthorityGrantResolutionStatus.ProfileUnavailable,
        AuthorityGrantOperationFailureCode.RoleUnavailable => AuthorityGrantResolutionStatus.RoleUnavailable,
        AuthorityGrantOperationFailureCode.LoopUnavailable => AuthorityGrantResolutionStatus.LoopUnavailable,
        AuthorityGrantOperationFailureCode.CeilingExceeded => AuthorityGrantResolutionStatus.CeilingExceeded,
        _ => AuthorityGrantResolutionStatus.Ambiguous,
    };

    private static AuthorityGrantResolution Result(
        AuthorityGrantResolutionStatus status,
        AuthorityGrantReference? reference,
        AuthorityGrant? grant = null,
        DateTimeOffset evaluatedAtUtc = default)
        => new(status, reference, grant, AuthorityCeilingIntersection.EmptyCeiling(), string.Empty, evaluatedAtUtc);

    private static bool IsValidReference(AuthorityGrantReference? reference)
        => reference?.GrantId is not null
            && reference.Revision is not null
            && AuthorityGrantId.TryParse(reference.GrantId.Value, out _, out _)
            && AuthorityGrantRevision.TryParse(reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
            && reference.ContentHash is { Length: 71 }
            && reference.ContentHash.StartsWith("sha256:", StringComparison.Ordinal)
            && reference.ContentHash[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AuthorityGrantReference? SafeReference(AuthorityGrantReference? reference) => IsValidReference(reference) ? reference : null;

    private static bool HasExactActiveProof(AuthorityGrantResolution? resolution)
        => resolution is
        {
            Status: AuthorityGrantResolutionStatus.Active,
            RequestedReference: { } reference,
            Grant: { } grant,
        }
            && IsValidReference(reference)
            && AuthorityGrantContractValidator.Validate(grant).IsValid
            && AuthorityGrantStoreSnapshotGuard.Matches(reference, grant)
            && AuthorityGrantEvidenceHash.IsSha256(resolution.DependencyEvidenceHash);
}
