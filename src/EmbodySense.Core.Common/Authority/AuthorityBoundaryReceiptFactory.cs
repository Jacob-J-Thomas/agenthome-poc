using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Creates boundary receipts only from closed, bounded, internally consistent evidence.
/// </summary>
public static class AuthorityBoundaryReceiptFactory
{
    /// <summary>
    /// Creates an immutable receipt when its schema, decision, conditions, profile references, and evaluation time are valid.
    /// </summary>
    public static bool TryCreate(int schemaVersion, AuthorityBoundaryDecision decision, IReadOnlyList<AuthorityBoundaryCondition>? conditions, IReadOnlyList<AuthorityProfileReference>? profiles, DateTimeOffset evaluatedAtUtc, out AuthorityBoundaryReceipt? receipt, out AuthorityContractValidationResult validation)
    {
        var errors = ValidateValues(schemaVersion, decision, conditions, profiles, evaluatedAtUtc, out var conditionSnapshot, out var profileSnapshot);
        validation = new AuthorityContractValidationResult(errors);
        if (!validation.IsValid)
        {
            receipt = null;
            return false;
        }

        var canonicalConditions = conditionSnapshot.OrderBy(condition => condition.Decision).ThenBy(condition => condition.Reason).ToArray();
        var canonicalProfiles = profileSnapshot.OrderBy(profile => profile.ProfileId).ThenBy(profile => profile.Revision).ToArray();
        receipt = new AuthorityBoundaryReceipt(schemaVersion, decision, Array.AsReadOnly(canonicalConditions), Array.AsReadOnly(canonicalProfiles), evaluatedAtUtc);
        return true;
    }

    /// <summary>
    /// Revalidates a receipt at a public boundary without copying caller-controlled evidence into a projection.
    /// </summary>
    public static AuthorityContractValidationResult Validate(AuthorityBoundaryReceipt? receipt)
    {
        if (receipt is null)
        {
            return new AuthorityContractValidationResult([new AuthorityContractError(AuthorityContractErrorCode.Required, AuthorityContractField.Contract)]);
        }

        return new AuthorityContractValidationResult(ValidateValues(receipt.SchemaVersion, receipt.Decision, receipt.Conditions, receipt.Profiles, receipt.EvaluatedAtUtc, out _, out _));
    }

    internal static AuthorityBoundaryReceipt CreateKnownValidDenial(DateTimeOffset evaluatedAtUtc)
    {
        var conditions = Array.AsReadOnly(new[] { new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.InvalidContract) });
        return new AuthorityBoundaryReceipt(AuthorityBoundaryReceipt.CurrentSchemaVersion, AuthorityBoundaryDecision.Deny, conditions, Array.AsReadOnly(Array.Empty<AuthorityProfileReference>()), evaluatedAtUtc);
    }

    private static IReadOnlyList<AuthorityContractError> ValidateValues(int schemaVersion, AuthorityBoundaryDecision decision, IReadOnlyList<AuthorityBoundaryCondition>? conditions, IReadOnlyList<AuthorityProfileReference>? profiles, DateTimeOffset evaluatedAtUtc, out AuthorityBoundaryCondition[] conditionSnapshot, out AuthorityProfileReference[] profileSnapshot)
    {
        var errors = new List<AuthorityContractError>();
        if (schemaVersion != AuthorityBoundaryReceipt.CurrentSchemaVersion)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.UnsupportedSchemaVersion, AuthorityContractField.SchemaVersion));
        }

        if (!Enum.IsDefined(decision) || decision == AuthorityBoundaryDecision.Unknown)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.UnsupportedBoundaryDecision, AuthorityContractField.BoundaryDecision));
        }

        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidEvaluationTime, AuthorityContractField.EvaluatedAtUtc));
        }

        conditionSnapshot = SnapshotConditions(conditions, errors);
        profileSnapshot = SnapshotProfiles(profiles, errors);
        if (profiles is not null && profiles.Count == 0 && decision != AuthorityBoundaryDecision.Deny)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidIntersectionProfiles, AuthorityContractField.Profiles));
        }

        if (conditionSnapshot.Length > 0 && conditionSnapshot.All(condition => AuthorityBoundaryConditionValidator.Validate(condition) is null))
        {
            if (conditionSnapshot.Any(condition => condition.Decision == AuthorityBoundaryDecision.Direct) && conditionSnapshot.Length != 1)
            {
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidBoundaryCondition, AuthorityContractField.BoundaryConditions));
            }
            else
            {
                var expectedDecision = conditionSnapshot.Max(condition => condition.Decision);
                if (decision != expectedDecision)
                {
                    errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidBoundaryCondition, AuthorityContractField.BoundaryDecision));
                }
            }
        }

        return errors.Distinct().ToArray();
    }

    private static AuthorityBoundaryCondition[] SnapshotConditions(IReadOnlyList<AuthorityBoundaryCondition>? conditions, List<AuthorityContractError> errors)
    {
        var conditionCount = conditions?.Count ?? 0;
        if (conditions is null || conditionCount is < 1 or > AuthorityContractLimits.MaxBoundaryConditionsPerReceipt)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.CollectionOutOfRange, AuthorityContractField.BoundaryConditions));
            return [];
        }

        var snapshot = new AuthorityBoundaryCondition[conditionCount];
        var seen = new HashSet<AuthorityBoundaryCondition>();
        for (var index = 0; index < conditionCount; index++)
        {
            var condition = conditions[index];
            snapshot[index] = condition;
            var error = AuthorityBoundaryConditionValidator.Validate(condition);
            if (error is not null)
            {
                errors.Add(error);
            }
            else if (!seen.Add(condition))
            {
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.DuplicateCollectionItem, AuthorityContractField.BoundaryConditions));
            }
        }

        return snapshot;
    }

    private static AuthorityProfileReference[] SnapshotProfiles(IReadOnlyList<AuthorityProfileReference>? profiles, List<AuthorityContractError> errors)
    {
        var profileCount = profiles?.Count ?? 0;
        if (profiles is null || profileCount > AuthorityContractLimits.MaxProfilesPerIntersection)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.CollectionOutOfRange, AuthorityContractField.Profiles));
            return [];
        }

        var snapshot = new AuthorityProfileReference[profileCount];
        var seen = new HashSet<AuthorityProfileReference>();
        for (var index = 0; index < profileCount; index++)
        {
            var profile = profiles[index];
            snapshot[index] = profile;
            if (profile?.ProfileId is null || profile.Revision is null)
            {
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.CollectionItemRequired, AuthorityContractField.Profiles));
            }
            else if (!seen.Add(profile))
            {
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.DuplicateProfileRevision, AuthorityContractField.Profiles));
            }
        }

        return snapshot;
    }
}
