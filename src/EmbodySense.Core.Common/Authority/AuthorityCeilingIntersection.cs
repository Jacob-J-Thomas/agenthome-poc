using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Intersects valid authority profiles monotonically without unioning capabilities, data classes, or permissive dimensions.
/// </summary>
public static class AuthorityCeilingIntersection
{
    /// <summary>
    /// Evaluates bounded profile declarations at an exact UTC instant without establishing a grant or executing an effect.
    /// </summary>
    /// <param name="profiles">The profiles supplied by an externally governed authority source.</param>
    /// <param name="evaluatedAtUtc">The exact UTC instant used for expiry and receipt evidence.</param>
    /// <returns>The candidate and effective ceilings, boundary receipt, and structured validation result.</returns>
    public static AuthorityIntersectionResult Evaluate(IReadOnlyList<AuthorityProfile>? profiles, DateTimeOffset evaluatedAtUtc)
    {
        var inputIsValid = AuthorityProfileSetValidator.TryValidateAndSnapshot(profiles, evaluatedAtUtc, out var snapshot, out var inputValidation);
        var errors = inputValidation.Errors.ToList();
        if (!inputIsValid)
        {
            return Denied(ToUniqueReferences(snapshot), evaluatedAtUtc, errors, AuthorityBoundaryReason.InvalidContract);
        }

        var candidate = IntersectCeilings(snapshot.Select(profile => profile.Ceiling));
        var conditions = snapshot.SelectMany(profile => profile.BoundaryConditions).ToList();
        foreach (var profile in snapshot)
        {
            if (profile.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= evaluatedAtUtc)
            {
                conditions.Add(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileExpired));
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.Expired, AuthorityContractField.ExpiresAtUtc));
            }

            switch (profile.Status)
            {
                case AuthorityProfileStatus.Draft:
                    conditions.Add(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.ProfileDraft));
                    break;
                case AuthorityProfileStatus.Suspended:
                    conditions.Add(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.ProfileSuspended));
                    break;
                case AuthorityProfileStatus.Retired:
                    conditions.Add(new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileRetired));
                    break;
            }
        }

        var canonicalConditions = CanonicalConditions(conditions);
        var decision = canonicalConditions.MaxBy(condition => condition.Decision)?.Decision ?? AuthorityBoundaryDecision.Direct;
        if (canonicalConditions.Count == 0)
        {
            canonicalConditions = [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)];
        }

        var references = ToUniqueReferences(snapshot);
        if (!AuthorityBoundaryReceiptFactory.TryCreate(AuthorityBoundaryReceipt.CurrentSchemaVersion, decision, canonicalConditions, references, evaluatedAtUtc, out var receipt, out var receiptValidation))
        {
            errors.AddRange(receiptValidation.Errors);
            receipt = AuthorityBoundaryReceiptFactory.CreateKnownValidDenial(evaluatedAtUtc.ToUniversalTime());
        }

        var effective = receipt!.Decision == AuthorityBoundaryDecision.Direct && errors.Count == 0 ? candidate : EmptyCeiling();
        return new AuthorityIntersectionResult(candidate, effective, receipt, new AuthorityContractValidationResult(errors.Distinct().ToArray()));
    }

    /// <summary>
    /// Creates an empty authority ceiling that cannot permit targets, capability identities, data classes, recurrence, publication, or irreversible action.
    /// </summary>
    /// <returns>The most restrictive schema-version-1 ceiling.</returns>
    public static AuthorityCeiling EmptyCeiling()
    {
        return new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, false, false, false);
    }

    private static AuthorityIntersectionResult Denied(IReadOnlyList<AuthorityProfileReference> profiles, DateTimeOffset evaluatedAtUtc, IReadOnlyList<AuthorityContractError> errors, AuthorityBoundaryReason reason)
    {
        var combinedErrors = errors.ToList();
        var conditions = new[] { new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, reason) };
        var canonicalProfiles = profiles.Distinct().OrderBy(reference => reference.ProfileId).ThenBy(reference => reference.Revision).ToArray();
        if (!AuthorityBoundaryReceiptFactory.TryCreate(AuthorityBoundaryReceipt.CurrentSchemaVersion, AuthorityBoundaryDecision.Deny, conditions, canonicalProfiles, evaluatedAtUtc.ToUniversalTime(), out var receipt, out var receiptValidation))
        {
            combinedErrors.AddRange(receiptValidation.Errors);
            receipt = AuthorityBoundaryReceiptFactory.CreateKnownValidDenial(evaluatedAtUtc.ToUniversalTime());
        }

        var empty = EmptyCeiling();
        return new AuthorityIntersectionResult(empty, empty, receipt!, new AuthorityContractValidationResult(combinedErrors.Distinct().ToArray()));
    }

    private static AuthorityProfileReference ToReference(AuthorityProfile profile)
    {
        return new AuthorityProfileReference(profile.ProfileId, profile.Revision);
    }

    private static IReadOnlyList<AuthorityProfileReference> ToUniqueReferences(IEnumerable<AuthorityProfile> profiles)
    {
        return profiles
            .Where(profile => profile?.ProfileId is not null && profile.Revision is not null)
            .Select(ToReference)
            .Distinct()
            .OrderBy(reference => reference.ProfileId)
            .ThenBy(reference => reference.Revision)
            .ToArray();
    }

    private static AuthorityCeiling IntersectCeilings(IEnumerable<AuthorityCeiling> ceilings)
    {
        using var enumerator = ceilings.Select(CanonicalizeCeiling).GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return EmptyCeiling();
        }

        var current = enumerator.Current;
        while (enumerator.MoveNext())
        {
            var next = enumerator.Current;
            current = CanonicalizeCeiling(new AuthorityCeiling(
                current.Capabilities.Intersect(next.Capabilities).ToArray(),
                current.DataClasses.Intersect(next.DataClasses).ToArray(),
                Math.Min(current.MaxTargetCount, next.MaxTargetCount),
                (CapabilitySideEffectClass)Math.Min((int)current.MaxSideEffectClass, (int)next.MaxSideEffectClass),
                current.AllowsRecurrence && next.AllowsRecurrence,
                current.AllowsExternalPublication && next.AllowsExternalPublication,
                current.AllowsIrreversibleAction && next.AllowsIrreversibleAction));
        }

        return current;
    }

    private static List<AuthorityBoundaryCondition> CanonicalConditions(IEnumerable<AuthorityBoundaryCondition> conditions)
    {
        return conditions.Where(condition => condition.Decision != AuthorityBoundaryDecision.Direct).Distinct().OrderBy(condition => condition.Decision).ThenBy(condition => condition.Reason).ToList();
    }

    private static AuthorityCeiling CanonicalizeCeiling(AuthorityCeiling ceiling)
    {
        return new AuthorityCeiling(
            ceiling.Capabilities.OrderBy(identity => identity.Id.Value, StringComparer.Ordinal).ThenBy(identity => identity.Version.Value, StringComparer.Ordinal).ThenBy(identity => identity.Hash.Value, StringComparer.Ordinal).ToArray(),
            ceiling.DataClasses.OrderBy(dataClass => dataClass.Value, StringComparer.Ordinal).ToArray(),
            ceiling.MaxTargetCount,
            ceiling.MaxSideEffectClass,
            ceiling.AllowsRecurrence,
            ceiling.AllowsExternalPublication,
            ceiling.AllowsIrreversibleAction);
    }
}
