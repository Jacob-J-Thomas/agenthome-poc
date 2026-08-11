using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves exact contextual-role revision pins over current role revision and lifecycle readers.</summary>
public sealed class AuthorityGrantRoleSource : IAuthorityGrantRoleSource
{
    private readonly string _workspaceId;
    private readonly IContextualRoleRevisionReader _revisionReader;
    private readonly IContextualRoleLifecycleReader _lifecycleReader;
    private readonly IContextualRoleInstructionSourceProbe _sourceProbe;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a workspace-bound exact role source over immutable revision, lifecycle, and registered-source evidence.</summary>
    /// <param name="workspaceId">The canonical workspace scope against which role applicability is evaluated.</param>
    /// <param name="revisionReader">The exact immutable role-revision reader.</param>
    /// <param name="lifecycleReader">The current role-lifecycle reader.</param>
    /// <param name="sourceProbe">The registered value-free instruction-source probe.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workspaceId"/> is not canonical.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required port is <see langword="null"/>.</exception>
    public AuthorityGrantRoleSource(
        string workspaceId,
        IContextualRoleRevisionReader revisionReader,
        IContextualRoleLifecycleReader lifecycleReader,
        IContextualRoleInstructionSourceProbe sourceProbe,
        ICapabilityAuthorityTransaction authorityTransaction)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("Workspace id must use the canonical workspace-sha256 scope contract.", nameof(workspaceId));
        }

        _workspaceId = workspaceId;
        _revisionReader = revisionReader ?? throw new ArgumentNullException(nameof(revisionReader));
        _lifecycleReader = lifecycleReader ?? throw new ArgumentNullException(nameof(lifecycleReader));
        _sourceProbe = sourceProbe ?? throw new ArgumentNullException(nameof(sourceProbe));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantRoleResolution> ResolveAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken = default)
    {
        AuthorityGrantRoleResolution? completedResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completedResult = await ResolveUnderFenceAsync(pin, transactionToken).ConfigureAwait(false);
                    return completedResult;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completedResult is null)
        {
            throw;
        }
        catch (Exception)
        {
            if (HasExactResolvedProof(completedResult))
            {
                return completedResult!;
            }

            return Result(
                completedResult is null ? AuthorityGrantDependencyStatus.Unavailable : AuthorityGrantDependencyStatus.Ambiguous,
                SafePin(pin));
        }
    }

    private async Task<AuthorityGrantRoleResolution> ResolveUnderFenceAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken)
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

        if (revisionRead.Disposition == ContextualRoleRevisionDisposition.Replaced)
        {
            return Equals(lifecycle!.CurrentIdentity, pin.Identity)
                ? Result(AuthorityGrantDependencyStatus.Ambiguous, pin)
                : Resolved(AuthorityGrantDependencyStatus.Stale, pin, revision, lifecycle, ContextualRoleInstructionSourceProbeStatus.Unknown);
        }

        if (!Equals(lifecycle!.CurrentIdentity, pin.Identity))
        {
            return Resolved(AuthorityGrantDependencyStatus.Stale, pin, revision, lifecycle, ContextualRoleInstructionSourceProbeStatus.Unknown);
        }

        if (lifecycle.State != ContextualRoleLifecycleState.Active
            || revision.Status != ContextualRoleStatus.Published
            || revisionRead.Disposition != ContextualRoleRevisionDisposition.Active)
        {
            return Resolved(AuthorityGrantDependencyStatus.Disabled, pin, revision, lifecycle, ContextualRoleInstructionSourceProbeStatus.Ineligible);
        }

        if (!revision.WorkspaceApplicability.AppliesTo(_workspaceId))
        {
            return Resolved(AuthorityGrantDependencyStatus.Disabled, pin, revision, lifecycle, ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch);
        }

        ContextualRoleInstructionSourceProbeStatus sourceStatus;
        try
        {
            sourceStatus = (await _sourceProbe.ProbeAsync(revision.InstructionSource, cancellationToken).ConfigureAwait(false))?.Status
                ?? ContextualRoleInstructionSourceProbeStatus.Ambiguous;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        var confirmation = await ReadLifecycleAsync(pin.Identity.RoleId, cancellationToken).ConfigureAwait(false);
        if (confirmation.Status is AuthorityGrantDependencyStatus.Unavailable or AuthorityGrantDependencyStatus.Ambiguous)
        {
            return Result(confirmation.Status, pin);
        }

        if (confirmation.Lifecycle is null)
        {
            return Result(AuthorityGrantDependencyStatus.Stale, pin);
        }

        if (!Equals(confirmation.Lifecycle, lifecycle))
        {
            return Resolved(AuthorityGrantDependencyStatus.Stale, pin, revision, confirmation.Lifecycle, sourceStatus);
        }

        return Resolved(MapSourceStatus(sourceStatus), pin, revision, lifecycle, sourceStatus);
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

    private async Task<(AuthorityGrantDependencyStatus Status, ContextualRoleLifecycleSnapshot? Lifecycle)> ReadLifecycleAsync(string roleId, CancellationToken cancellationToken)
    {
        ContextualRoleLifecycleReadResult? read;
        try
        {
            read = await _lifecycleReader.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(roleId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (AuthorityGrantDependencyStatus.Unavailable, null);
        }

        if (read is null || !Enum.IsDefined(read.Status) || read.Status == ContextualRoleLifecycleReadStatus.Unknown)
        {
            return (AuthorityGrantDependencyStatus.Ambiguous, null);
        }

        return read.Status switch
        {
            ContextualRoleLifecycleReadStatus.Found when IsValidLifecycle(read.Snapshot, roleId) => (AuthorityGrantDependencyStatus.Active, read.Snapshot),
            ContextualRoleLifecycleReadStatus.NotFound when read.Snapshot is null => (AuthorityGrantDependencyStatus.NotFound, null),
            ContextualRoleLifecycleReadStatus.Unavailable when read.Snapshot is null => (AuthorityGrantDependencyStatus.Unavailable, null),
            _ => (AuthorityGrantDependencyStatus.Ambiguous, null),
        };
    }

    private AuthorityGrantRoleResolution Resolved(
        AuthorityGrantDependencyStatus status,
        ContextualRoleRevisionPin pin,
        ContextualRoleRevision revision,
        ContextualRoleLifecycleSnapshot? lifecycle,
        ContextualRoleInstructionSourceProbeStatus sourceStatus)
    {
        var evidence = AuthorityGrantEvidenceHash.Compute(
            _workspaceId,
            pin.Identity.RoleId,
            pin.Identity.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pin.ContentHash,
            revision.InstructionSource.Kind.ToString(),
            revision.InstructionSource.ReferenceId,
            sourceStatus.ToString(),
            lifecycle?.LastOperationId ?? string.Empty,
            lifecycle?.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return new AuthorityGrantRoleResolution(status, pin, revision, lifecycle, _workspaceId, sourceStatus, evidence);
    }

    private AuthorityGrantRoleResolution Result(AuthorityGrantDependencyStatus status, ContextualRoleRevisionPin? pin)
        => new(status, pin, null, null, _workspaceId, ContextualRoleInstructionSourceProbeStatus.Unknown, string.Empty);

    private static AuthorityGrantDependencyStatus MapSourceStatus(ContextualRoleInstructionSourceProbeStatus status)
        => status switch
        {
            ContextualRoleInstructionSourceProbeStatus.Ready => AuthorityGrantDependencyStatus.Active,
            ContextualRoleInstructionSourceProbeStatus.Missing => AuthorityGrantDependencyStatus.NotFound,
            ContextualRoleInstructionSourceProbeStatus.Unavailable => AuthorityGrantDependencyStatus.Unavailable,
            ContextualRoleInstructionSourceProbeStatus.Ineligible => AuthorityGrantDependencyStatus.Disabled,
            _ => AuthorityGrantDependencyStatus.Ambiguous,
        };

    private bool HasExactResolvedProof(AuthorityGrantRoleResolution? result)
        => result is
        {
            Status: AuthorityGrantDependencyStatus.Active,
            RequestedPin: { } pin,
            Revision: { } revision,
            Lifecycle: { } lifecycle,
            SourceStatus: ContextualRoleInstructionSourceProbeStatus.Ready,
        }
            && IsValidPin(pin)
            && Equals(revision.Identity, pin.Identity)
            && string.Equals(revision.ContentHash, pin.ContentHash, StringComparison.Ordinal)
            && ContextualRoleRevisionValidator.Validate(revision).IsValid
            && revision.WorkspaceApplicability.AppliesTo(_workspaceId)
            && string.Equals(result.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && IsValidLifecycle(lifecycle, pin.Identity.RoleId)
            && Equals(lifecycle.CurrentIdentity, pin.Identity)
            && lifecycle.State == ContextualRoleLifecycleState.Active
            && AuthorityGrantEvidenceHash.IsSha256(result.EvidenceHash);

    private static ContextualRoleRevisionPin? SafePin(ContextualRoleRevisionPin? pin) => IsValidPin(pin) ? pin : null;

    private static bool IsValidPin(ContextualRoleRevisionPin? pin)
        => pin?.Identity is not null
            && ContextualRoleId.IsValid(pin.Identity.RoleId)
            && pin.Identity.Revision > 0
            && pin.ContentHash is { Length: 64 }
            && pin.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
