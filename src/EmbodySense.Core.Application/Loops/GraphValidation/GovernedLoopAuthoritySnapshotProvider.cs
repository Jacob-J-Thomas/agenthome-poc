using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Projects exact workspace-, lifecycle-, and source-complete contextual-role evidence for graph validation.</summary>
public sealed class GovernedLoopAuthoritySnapshotProvider : IGovernedLoopAuthoritySnapshotProvider
{
    private readonly IAuthorityGrantRoleSource _roleSource;

    /// <summary>Creates a graph-authority provider over the shared exact contextual-role source.</summary>
    /// <param name="roleSource">The workspace-bound exact contextual-role source.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="roleSource"/> is <see langword="null"/>.</exception>
    public GovernedLoopAuthoritySnapshotProvider(IAuthorityGrantRoleSource roleSource)
    {
        _roleSource = roleSource ?? throw new ArgumentNullException(nameof(roleSource));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default)
    {
        AuthorityGrantRoleResolution? resolution;
        try
        {
            resolution = await _roleSource.ResolveAsync(owningRole, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(SafePin(owningRole));
        }

        if (!IsExactActive(resolution, owningRole))
        {
            return Unavailable(SafePin(owningRole));
        }

        return new GovernedLoopAuthoritySnapshot(
            true,
            resolution!.EvidenceHash,
            owningRole,
            resolution.Revision,
            resolution.Lifecycle,
            resolution.WorkspaceId,
            resolution.SourceStatus,
            resolution.Revision!.PolicyMaxima.CapabilityIds,
            CustomLoopLimits.MaxGraphNodeAttempts,
            CustomLoopLimits.MaxGraphNodePayloadCharacters,
            CustomLoopLimits.MaxGraphNodeEvidenceItems,
            CustomLoopLimits.MaxGraphNodeResourceUnits);
    }

    private static bool IsExactActive(AuthorityGrantRoleResolution? resolution, ContextualRoleRevisionPin? owningRole)
        => resolution is
        {
            Status: AuthorityGrantDependencyStatus.Active,
            RequestedPin: { } requestedPin,
            Revision: { } revision,
            Lifecycle: { } lifecycle,
            SourceStatus: ContextualRoleInstructionSourceProbeStatus.Ready,
        }
            && owningRole is not null
            && Equals(requestedPin, owningRole)
            && Equals(revision.Identity, owningRole.Identity)
            && string.Equals(revision.ContentHash, owningRole.ContentHash, StringComparison.Ordinal)
            && ContextualRoleRevisionValidator.Validate(revision).IsValid
            && ContextualRoleWorkspaceId.IsValid(resolution.WorkspaceId)
            && revision.WorkspaceApplicability.AppliesTo(resolution.WorkspaceId)
            && lifecycle.SchemaVersion == 1
            && string.Equals(lifecycle.RoleId, owningRole.Identity.RoleId, StringComparison.Ordinal)
            && lifecycle.State == ContextualRoleLifecycleState.Active
            && Equals(lifecycle.CurrentIdentity, owningRole.Identity)
            && ContextualRoleId.IsValid(lifecycle.LastOperationId)
            && Enum.IsDefined(lifecycle.LastMutationKind)
            && lifecycle.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown
            && lifecycle.UpdatedAtUtc != default
            && lifecycle.UpdatedAtUtc.Offset == TimeSpan.Zero
            && AuthorityGrantEvidenceHash.IsSha256(resolution.EvidenceHash);

    private static GovernedLoopAuthoritySnapshot Unavailable(ContextualRoleRevisionPin? owningRole)
        => new(
            false,
            string.Empty,
            owningRole,
            null,
            null,
            string.Empty,
            ContextualRoleInstructionSourceProbeStatus.Unknown,
            [],
            0,
            0,
            0,
            0);

    private static ContextualRoleRevisionPin? SafePin(ContextualRoleRevisionPin? pin)
        => pin?.Identity is not null
            && ContextualRoleId.IsValid(pin.Identity.RoleId)
            && pin.Identity.Revision > 0
            && pin.ContentHash is { Length: 64 }
            && pin.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? pin
                : null;
}
