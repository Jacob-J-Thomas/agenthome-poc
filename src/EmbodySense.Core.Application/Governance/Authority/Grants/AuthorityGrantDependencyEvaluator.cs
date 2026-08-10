using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

internal sealed class AuthorityGrantDependencyEvaluator
{
    private readonly IAuthorityGrantProfileSource _profileSource;
    private readonly IAuthorityGrantRoleSource _roleSource;
    private readonly IGovernedLoopPublishedRevisionSource _publishedLoopSource;
    private readonly IGovernedLoopGrantBindingSource _loopBindingSource;

    internal AuthorityGrantDependencyEvaluator(
        IAuthorityGrantProfileSource profileSource,
        IAuthorityGrantRoleSource roleSource,
        IGovernedLoopPublishedRevisionSource publishedLoopSource,
        IGovernedLoopGrantBindingSource loopBindingSource)
    {
        _profileSource = profileSource ?? throw new ArgumentNullException(nameof(profileSource));
        _roleSource = roleSource ?? throw new ArgumentNullException(nameof(roleSource));
        _publishedLoopSource = publishedLoopSource ?? throw new ArgumentNullException(nameof(publishedLoopSource));
        _loopBindingSource = loopBindingSource ?? throw new ArgumentNullException(nameof(loopBindingSource));
    }

    internal async Task<(AuthorityGrantOperationFailureCode FailureCode, string EvidenceHash)> EvaluateAsync(
        AuthorityGrantBinding binding,
        AuthorityCeiling ceiling,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        AuthorityGrantProfileResolution profile;
        try
        {
            profile = await _profileSource.ResolveAsync(binding.Profile, evaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (AuthorityGrantOperationFailureCode.ProfileUnavailable, string.Empty);
        }

        if (!IsExactActiveProfile(profile, binding.Profile))
        {
            return (AuthorityGrantOperationFailureCode.ProfileUnavailable, string.Empty);
        }

        AuthorityGrantRoleResolution role;
        try
        {
            role = await _roleSource.ResolveAsync(binding.Role, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (AuthorityGrantOperationFailureCode.RoleUnavailable, string.Empty);
        }

        if (!IsExactActiveRole(role, binding.Role, evaluatedAtUtc))
        {
            return (AuthorityGrantOperationFailureCode.RoleUnavailable, string.Empty);
        }

        GovernedLoopPublishedRevisionResolution publication;
        GovernedLoopGrantBindingResolution loopBinding;
        try
        {
            publication = await _publishedLoopSource.ResolveAsync(binding.Loop, cancellationToken).ConfigureAwait(false);
            loopBinding = await _loopBindingSource.ResolveAsync(binding.Loop, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (AuthorityGrantOperationFailureCode.LoopUnavailable, string.Empty);
        }

        if (!IsExactActiveLoop(publication, loopBinding, binding))
        {
            return (AuthorityGrantOperationFailureCode.LoopUnavailable, string.Empty);
        }

        var publicationEvidence = AuthorityGrantEvidenceHash.Compute(
            binding.Loop.Revision.GraphId,
            binding.Loop.Revision.RevisionId,
            binding.Loop.Revision.ExecutableHash,
            binding.Loop.PublicationOperationId,
            binding.Loop.ValidationEvidenceHash,
            publication.ObservedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            publication.ObservedLifecycleHeadOperationId);
        var evidence = AuthorityGrantEvidenceHash.Compute(profile.EvidenceHash, role.EvidenceHash, publicationEvidence, loopBinding.EvidenceHash);
        var subset = AuthorityCeilingSubset.Validate(
            ceiling,
            profile.Profile!.Ceiling,
            role.Revision!.PolicyMaxima.CapabilityIds.ToArray(),
            loopBinding.CapabilityIds);
        if (!subset.IsSubset)
        {
            return (AuthorityGrantOperationFailureCode.CeilingExceeded, evidence);
        }

        return (AuthorityGrantOperationFailureCode.None, evidence);
    }

    private static bool IsExactActiveProfile(AuthorityGrantProfileResolution? resolution, AuthorityGrantProfilePin pin)
    {
        if (resolution is not
            {
                Status: AuthorityGrantDependencyStatus.Active,
                RequestedPin: { } observedPin,
                Profile: { } profile,
            }
            || !Equals(observedPin, pin)
            || !AuthorityGrantEvidenceHash.IsSha256(resolution.EvidenceHash)
            || !profile.ProfileId.Equals(pin.Reference.ProfileId)
            || !profile.Revision.Equals(pin.Reference.Revision)
            || !AuthorityProfileHash.TryCompute(profile, out var hash, out var validation)
            || !validation.IsValid)
        {
            return false;
        }

        return hash!.Equals(pin.ContentHash) && profile.Status == AuthorityProfileStatus.Active;
    }

    private static bool IsExactActiveRole(
        AuthorityGrantRoleResolution? resolution,
        ContextualRoleRevisionPin pin,
        DateTimeOffset evaluatedAtUtc)
        => resolution is
        {
            Status: AuthorityGrantDependencyStatus.Active,
            RequestedPin: { } observedPin,
            Revision: { } revision,
            Lifecycle: { } lifecycle,
        }
            && Equals(observedPin, pin)
            && AuthorityGrantEvidenceHash.IsSha256(resolution.EvidenceHash)
            && Equals(revision.Identity, pin.Identity)
            && string.Equals(revision.ContentHash, pin.ContentHash, StringComparison.Ordinal)
            && ContextualRoleRevisionValidator.Validate(revision).IsValid
            && Equals(lifecycle.CurrentIdentity, pin.Identity)
            && lifecycle.State == ContextualRoleLifecycleState.Active
            && lifecycle.UpdatedAtUtc != default
            && lifecycle.UpdatedAtUtc.Offset == TimeSpan.Zero
            && lifecycle.UpdatedAtUtc <= evaluatedAtUtc;

    private static bool IsExactActiveLoop(
        GovernedLoopPublishedRevisionResolution? publication,
        GovernedLoopGrantBindingResolution? loopBinding,
        AuthorityGrantBinding binding)
        => publication is
        {
            Status: GovernedLoopPublishedRevisionResolutionStatus.Active,
            RequestedPin: { } publicationPin,
            Artifact: { } artifact,
            ObservedLifecycleStatus: Common.Loops.Revisions.Models.GovernedLoopRevisionLifecycleStatus.Published,
            ObservedLifecycleVersion: > 0,
            ObservedLifecycleHeadOperationId: { } observedOperationId,
        }
            && Equals(publicationPin, binding.Loop)
            && CustomLoopArtifactIdentifier.IsValid(observedOperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            && GovernedLoopRevisionContractValidator.Validate(publicationPin).IsValid
            && GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
            && Equals(artifact.Revision, publicationPin.Revision)
            && loopBinding is
            {
                Status: AuthorityGrantDependencyStatus.Active,
                PublicationPin: { } bindingPin,
                OwningRole: { } owner,
                CapabilityIds: not null,
            }
            && Equals(bindingPin, binding.Loop)
            && Equals(owner, binding.Role.Identity)
            && IsCanonicalCapabilityIds(loopBinding.CapabilityIds)
            && AuthorityGrantEvidenceHash.IsSha256(loopBinding.EvidenceHash);

    private static bool IsCanonicalCapabilityIds(IReadOnlyList<string> values)
        => values.Count <= CustomLoopLimits.MaxGraphAuthorityCapabilities
            && values.All(value => CapabilityId.TryParse(value, out _, out _))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}
