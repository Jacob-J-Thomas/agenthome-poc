using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Evaluates requested authority monotonically against exact profile, role, and loop maxima.</summary>
public static class AuthorityCeilingSubset
{
    /// <summary>Validates that every requested dimension is within the exact supplied maxima.</summary>
    public static AuthorityCeilingSubsetResult Validate(
        AuthorityCeiling? requested,
        AuthorityCeiling? profileCeiling,
        IReadOnlyList<string>? roleCapabilityIds,
        IReadOnlyList<string>? loopCapabilityIds)
    {
        var violations = new List<AuthorityCeilingSubsetViolation>();
        if (!AuthorityProfileValidator.ValidateCeiling(requested).IsValid
            || !AuthorityProfileValidator.ValidateCeiling(profileCeiling).IsValid
            || !TrySnapshotCapabilityIds(roleCapabilityIds, ContextualRoleLimits.MaxCapabilityMaximums, out var roleIds)
            || !TrySnapshotCapabilityIds(loopCapabilityIds, CustomLoopLimits.MaxGraphAuthorityCapabilities, out var loopIds))
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.InvalidContract);
            return Result(violations);
        }

        var profileCapabilities = profileCeiling!.Capabilities.ToHashSet();
        var profileDataClasses = profileCeiling.DataClasses.ToHashSet();
        foreach (var capability in requested!.Capabilities)
        {
            if (!profileCapabilities.Contains(capability))
            {
                Add(violations, AuthorityCeilingSubsetViolationCode.CapabilityIdentityOutsideProfile);
            }

            if (!roleIds!.Contains(capability.Id.Value))
            {
                Add(violations, AuthorityCeilingSubsetViolationCode.CapabilityIdOutsideRole);
            }

            if (!loopIds!.Contains(capability.Id.Value))
            {
                Add(violations, AuthorityCeilingSubsetViolationCode.CapabilityIdOutsideLoop);
            }
        }

        if (requested.DataClasses.Any(dataClass => !profileDataClasses.Contains(dataClass)))
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.DataClassOutsideProfile);
        }

        if (requested.MaxTargetCount > profileCeiling.MaxTargetCount)
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.TargetCountExceedsProfile);
        }

        if (requested.MaxSideEffectClass > profileCeiling.MaxSideEffectClass)
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.SideEffectClassExceedsProfile);
        }

        if (requested.AllowsRecurrence && !profileCeiling.AllowsRecurrence)
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.RecurrenceExceedsProfile);
        }

        if (requested.AllowsExternalPublication && !profileCeiling.AllowsExternalPublication)
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.ExternalPublicationExceedsProfile);
        }

        if (requested.AllowsIrreversibleAction && !profileCeiling.AllowsIrreversibleAction)
        {
            Add(violations, AuthorityCeilingSubsetViolationCode.IrreversibleActionExceedsProfile);
        }

        return Result(violations);
    }

    /// <summary>Gets whether the candidate is a valid strict subset of the current exact ceiling.</summary>
    public static bool IsStrictSubset(AuthorityCeiling? candidate, AuthorityCeiling? current)
    {
        if (!AuthorityProfileValidator.ValidateCeiling(current).IsValid)
        {
            return false;
        }

        var capabilityIds = current!.Capabilities.Select(identity => identity.Id.Value).Distinct(StringComparer.Ordinal).ToArray();
        return Validate(candidate, current, capabilityIds, capabilityIds).IsSubset && !IsEqual(candidate!, current);
    }

    /// <summary>Gets whether two valid ceilings contain exactly the same authority dimensions.</summary>
    public static bool IsEqual(AuthorityCeiling? left, AuthorityCeiling? right)
    {
        return AuthorityProfileValidator.ValidateCeiling(left).IsValid
            && AuthorityProfileValidator.ValidateCeiling(right).IsValid
            && left!.Capabilities.ToHashSet().SetEquals(right!.Capabilities)
            && left.DataClasses.ToHashSet().SetEquals(right.DataClasses)
            && left.MaxTargetCount == right.MaxTargetCount
            && left.MaxSideEffectClass == right.MaxSideEffectClass
            && left.AllowsRecurrence == right.AllowsRecurrence
            && left.AllowsExternalPublication == right.AllowsExternalPublication
            && left.AllowsIrreversibleAction == right.AllowsIrreversibleAction;
    }

    private static bool TrySnapshotCapabilityIds(IReadOnlyList<string>? values, int maximumCount, out HashSet<string>? snapshot)
    {
        snapshot = null;
        if (values is null)
        {
            return false;
        }

        try
        {
            var declaredCount = values.Count;
            if (declaredCount is < 0 || declaredCount > maximumCount)
            {
                return false;
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            var observedCount = 0;
            foreach (var value in values)
            {
                if (observedCount == maximumCount
                    || !CapabilityId.TryParse(value, out _, out _)
                    || !result.Add(value))
                {
                    return false;
                }

                observedCount++;
            }

            if (observedCount != declaredCount)
            {
                return false;
            }

            snapshot = result;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Add(List<AuthorityCeilingSubsetViolation> violations, AuthorityCeilingSubsetViolationCode code)
    {
        var violation = new AuthorityCeilingSubsetViolation(code);
        if (!violations.Contains(violation))
        {
            violations.Add(violation);
        }
    }

    private static AuthorityCeilingSubsetResult Result(IReadOnlyList<AuthorityCeilingSubsetViolation> violations)
    {
        var snapshot = Array.AsReadOnly(violations.Distinct().ToArray());
        return new AuthorityCeilingSubsetResult(snapshot, snapshot.Count == 0);
    }
}
