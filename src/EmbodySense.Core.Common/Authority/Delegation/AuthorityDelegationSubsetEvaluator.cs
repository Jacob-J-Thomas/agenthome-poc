using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Recomputes monotonic delegated authority against exact parent and target maxima.</summary>
public static class AuthorityDelegationSubsetEvaluator
{
    /// <summary>Creates a hash-only proof only when every delegated dimension and pin is equal or narrower.</summary>
    /// <param name="parentCeiling">The immutable parent effective authority ceiling.</param>
    /// <param name="parentPins">The exact parent pins that describe that ceiling.</param>
    /// <param name="targetRoleCapabilityIds">The exact target-role capability-id maximum.</param>
    /// <param name="targetLoopCapabilityIds">The exact target-loop capability-id maximum, or the role maximum for a role target.</param>
    /// <param name="targetNodeCapabilityIds">The exact target-node capability-id maximum, or the nearest enclosing maximum when no node applies.</param>
    /// <param name="delegatedCeiling">The requested delegated authority ceiling.</param>
    /// <param name="delegatedPins">The exact pins describing the delegated ceiling.</param>
    /// <param name="parentEvidenceHash">The canonical exact parent-evidence hash.</param>
    /// <param name="targetMaximumEvidenceHash">The server-resolved exact target-maximum evidence hash.</param>
    /// <returns>A canonical proof when every relation is monotonic; otherwise, <see langword="null"/>.</returns>
    public static AuthorityDelegationSubsetProof? Evaluate(
        AuthorityCeiling? parentCeiling,
        IReadOnlyList<CapabilityAdmissionPin>? parentPins,
        IReadOnlyList<string>? targetRoleCapabilityIds,
        IReadOnlyList<string>? targetLoopCapabilityIds,
        IReadOnlyList<string>? targetNodeCapabilityIds,
        AuthorityCeiling? delegatedCeiling,
        IReadOnlyList<CapabilityAdmissionPin>? delegatedPins,
        string? parentEvidenceHash,
        string? targetMaximumEvidenceHash)
    {
        try
        {
            if (!AuthorityDelegationContractValidator.ValidateAuthorityScopeForHash(parentCeiling, parentPins).IsValid
                || !AuthorityDelegationContractValidator.ValidateAuthorityScopeForHash(delegatedCeiling, delegatedPins).IsValid
                || !AuthorityDelegationContractHash.IsCanonicalHash(parentEvidenceHash)
                || !AuthorityDelegationContractHash.IsCanonicalHash(targetMaximumEvidenceHash)
                || !TryCapabilityIds(targetRoleCapabilityIds, ContextualRoleLimits.MaxCapabilityMaximums, out var roleIds)
                || !TryCapabilityIds(targetLoopCapabilityIds, CustomLoopLimits.MaxGraphAuthorityCapabilities, out var loopIds)
                || !TryCapabilityIds(targetNodeCapabilityIds, CustomLoopLimits.MaxGraphAuthorityCapabilities, out var nodeIds))
            {
                return null;
            }

            var roleIdSet = roleIds!.ToHashSet(StringComparer.Ordinal);
            var loopIdSet = loopIds!.ToHashSet(StringComparer.Ordinal);
            var nodeIdSet = nodeIds!.ToHashSet(StringComparer.Ordinal);
            if (!loopIdSet.IsSubsetOf(roleIdSet) || !nodeIdSet.IsSubsetOf(loopIdSet))
            {
                return null;
            }

            var relation = AuthorityCeilingSubset.Validate(delegatedCeiling, parentCeiling, roleIds!, loopIds!);
            if (!relation.IsSubset || delegatedCeiling!.Capabilities.Any(capability => !nodeIdSet.Contains(capability.Id.Value)))
            {
                return null;
            }

            var parentPinSet = parentPins!.ToHashSet();
            if (delegatedPins!.Any(pin => !parentPinSet.Contains(pin)))
            {
                return null;
            }

            var dimensions = DetermineNarrowingDimensions(parentCeiling!, delegatedCeiling);
            var candidate = new AuthorityDelegationSubsetProof(
                parentEvidenceHash!,
                AuthorityDelegationContractHash.ComputeAuthorityScopeHash(parentCeiling!, parentPins!),
                AuthorityDelegationContractHash.ComputeAuthorityScopeHash(delegatedCeiling, delegatedPins!),
                targetMaximumEvidenceHash!,
                dimensions,
                string.Empty);
            return AuthorityDelegationContractHash.Apply(candidate);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<AuthorityDelegationNarrowingDimension> DetermineNarrowingDimensions(AuthorityCeiling parent, AuthorityCeiling delegated)
    {
        var dimensions = new List<AuthorityDelegationNarrowingDimension>();
        if (!parent.Capabilities.ToHashSet().SetEquals(delegated.Capabilities))
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.CapabilityIdentitySet);
        }

        if (!parent.DataClasses.ToHashSet().SetEquals(delegated.DataClasses))
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.DataClassSet);
        }

        if (delegated.MaxTargetCount < parent.MaxTargetCount)
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.TargetCount);
        }

        if (delegated.MaxSideEffectClass < parent.MaxSideEffectClass)
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.SideEffectClass);
        }

        if (parent.AllowsRecurrence && !delegated.AllowsRecurrence)
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.Recurrence);
        }

        if (parent.AllowsExternalPublication && !delegated.AllowsExternalPublication)
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.ExternalPublication);
        }

        if (parent.AllowsIrreversibleAction && !delegated.AllowsIrreversibleAction)
        {
            dimensions.Add(AuthorityDelegationNarrowingDimension.IrreversibleAction);
        }

        return Array.AsReadOnly(dimensions.ToArray());
    }

    private static bool TryCapabilityIds(IReadOnlyList<string>? values, int maximum, out IReadOnlyList<string>? snapshot)
    {
        snapshot = AuthorityDelegationContractCopy.Snapshot(values, maximum);
        if (snapshot is null)
        {
            return false;
        }

        string? previous = null;
        foreach (var value in snapshot)
        {
            if (!CapabilityId.TryParse(value, out _, out _)
                || previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }
}
