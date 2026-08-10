using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves exact contextual-role revision pins over current role revision and lifecycle readers.</summary>
public sealed class AuthorityGrantRoleSource : IAuthorityGrantRoleSource
{
    private readonly IContextualRoleRevisionReader _revisionReader;
    private readonly IContextualRoleLifecycleReader _lifecycleReader;

    /// <summary>Creates an exact role source over immutable revision and current lifecycle readers.</summary>
    public AuthorityGrantRoleSource(IContextualRoleRevisionReader revisionReader, IContextualRoleLifecycleReader lifecycleReader)
    {
        _revisionReader = revisionReader ?? throw new ArgumentNullException(nameof(revisionReader));
        _lifecycleReader = lifecycleReader ?? throw new ArgumentNullException(nameof(lifecycleReader));
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantRoleResolution> ResolveAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken = default)
    {
        if (!IsValidPin(pin))
        {
            return Result(AuthorityGrantDependencyStatus.Invalid, null);
        }

        ContextualRoleRevisionReadResult revisionRead;
        try
        {
            revisionRead = await _revisionReader.ReadAsync(new ContextualRoleRevisionReadRequest(pin!.Identity), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        var mapped = MapRevisionRead(revisionRead);
        if (mapped is not null)
        {
            return Result(mapped.Value, pin);
        }

        var revision = revisionRead.Revision!;
        if (!Equals(revision.Identity, pin!.Identity)
            || !string.Equals(revision.ContentHash, pin.ContentHash, StringComparison.Ordinal)
            || !ContextualRoleRevisionValidator.Validate(revision).IsValid
            || revisionRead.ValidationErrors is null
            || revisionRead.ValidationErrors.Count != 0
            || !Enum.IsDefined(revisionRead.Disposition)
            || revisionRead.Disposition == ContextualRoleRevisionDisposition.Unknown)
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (revisionRead.Disposition == ContextualRoleRevisionDisposition.Replaced)
        {
            return Resolved(AuthorityGrantDependencyStatus.Stale, pin, revision, null);
        }

        ContextualRoleLifecycleReadResult lifecycleRead;
        try
        {
            lifecycleRead = await _lifecycleReader.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(pin.Identity.RoleId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        if (lifecycleRead is null || !Enum.IsDefined(lifecycleRead.Status) || lifecycleRead.Status == ContextualRoleLifecycleReadStatus.Unknown)
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (lifecycleRead.Status == ContextualRoleLifecycleReadStatus.NotFound && lifecycleRead.Snapshot is null)
        {
            return Result(AuthorityGrantDependencyStatus.NotFound, pin);
        }

        if (lifecycleRead.Status == ContextualRoleLifecycleReadStatus.Unavailable && lifecycleRead.Snapshot is null)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        var lifecycle = lifecycleRead.Snapshot;
        if (lifecycleRead.Status != ContextualRoleLifecycleReadStatus.Found || !IsValidLifecycle(lifecycle, pin.Identity.RoleId))
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (!Equals(lifecycle!.CurrentIdentity, pin.Identity))
        {
            return Resolved(AuthorityGrantDependencyStatus.Stale, pin, revision, lifecycle);
        }

        if (lifecycle.State != ContextualRoleLifecycleState.Active
            || revision.Status != ContextualRoleStatus.Published
            || revisionRead.Disposition != ContextualRoleRevisionDisposition.Active)
        {
            return Resolved(AuthorityGrantDependencyStatus.Disabled, pin, revision, lifecycle);
        }

        return Resolved(AuthorityGrantDependencyStatus.Active, pin, revision, lifecycle);
    }

    private static AuthorityGrantDependencyStatus? MapRevisionRead(ContextualRoleRevisionReadResult? read)
    {
        if (read is null || !Enum.IsDefined(read.Status) || read.Status == ContextualRoleRevisionReadStatus.Unknown)
        {
            return AuthorityGrantDependencyStatus.Ambiguous;
        }

        return read.Status switch
        {
            ContextualRoleRevisionReadStatus.Found when read.Revision is not null && read.ValidationErrors is { Count: 0 } => null,
            ContextualRoleRevisionReadStatus.NotFound when read.Revision is null => AuthorityGrantDependencyStatus.NotFound,
            ContextualRoleRevisionReadStatus.Invalid when read.Revision is null => AuthorityGrantDependencyStatus.Invalid,
            ContextualRoleRevisionReadStatus.Unavailable when read.Revision is null => AuthorityGrantDependencyStatus.Unavailable,
            _ => AuthorityGrantDependencyStatus.Ambiguous,
        };
    }

    private static bool IsValidLifecycle(ContextualRoleLifecycleSnapshot? value, string roleId)
        => value is
        {
            SchemaVersion: 1,
            CurrentIdentity: not null,
            LastOperationId.Length: > 0,
        }
            && value.UpdatedAtUtc != default
            && value.UpdatedAtUtc.Offset == TimeSpan.Zero
            && string.Equals(value.RoleId, roleId, StringComparison.Ordinal)
            && string.Equals(value.CurrentIdentity.RoleId, roleId, StringComparison.Ordinal)
            && value.CurrentIdentity.Revision > 0
            && ContextualRoleId.IsValid(value.LastOperationId)
            && Enum.IsDefined(value.State)
            && value.State != ContextualRoleLifecycleState.Unknown
            && Enum.IsDefined(value.LastMutationKind)
            && value.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown;

    private static AuthorityGrantRoleResolution Resolved(AuthorityGrantDependencyStatus status, ContextualRoleRevisionPin pin, ContextualRoleRevision revision, ContextualRoleLifecycleSnapshot? lifecycle)
    {
        var evidence = AuthorityGrantEvidenceHash.Compute(pin.Identity.RoleId, pin.Identity.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), pin.ContentHash, lifecycle?.LastOperationId ?? string.Empty, lifecycle?.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return new AuthorityGrantRoleResolution(status, pin, revision, lifecycle, evidence);
    }

    private static AuthorityGrantRoleResolution Result(AuthorityGrantDependencyStatus status, ContextualRoleRevisionPin? pin)
        => new(status, pin, null, null, string.Empty);

    private static bool IsValidPin(ContextualRoleRevisionPin? pin)
        => pin?.Identity is not null
            && ContextualRoleId.IsValid(pin.Identity.RoleId)
            && pin.Identity.Revision > 0
            && pin.ContentHash is { Length: 64 }
            && pin.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
