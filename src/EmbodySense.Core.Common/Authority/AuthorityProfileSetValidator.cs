using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Validates and snapshots a bounded authority-profile evaluation input before any intersection or application-port crossing.
/// </summary>
public static class AuthorityProfileSetValidator
{
    /// <summary>
    /// Creates one defensive snapshot only when the collection, profiles, evaluation time, and profile-revision identities are valid.
    /// </summary>
    /// <param name="profiles">The candidate profiles to validate and snapshot.</param>
    /// <param name="evaluatedAtUtc">The exact UTC evaluation instant.</param>
    /// <param name="snapshot">The bounded defensive snapshot, including structurally invalid entries when available for value-free denial evidence.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> only when the complete input is valid and contains unique profile revisions.</returns>
    public static bool TryValidateAndSnapshot(IReadOnlyList<AuthorityProfile>? profiles, DateTimeOffset evaluatedAtUtc, out IReadOnlyList<AuthorityProfile> snapshot, out AuthorityContractValidationResult validation)
    {
        var errors = new List<AuthorityContractError>();
        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidEvaluationTime, AuthorityContractField.EvaluatedAtUtc));
        }

        var profileCount = profiles?.Count ?? 0;
        if (profiles is null || profileCount is < 1 or > AuthorityContractLimits.MaxProfilesPerIntersection)
        {
            errors.Add(new AuthorityContractError(AuthorityContractErrorCode.InvalidIntersectionProfiles, AuthorityContractField.Profiles));
            snapshot = Array.Empty<AuthorityProfile>();
            validation = new AuthorityContractValidationResult(errors.Distinct().ToArray());
            return false;
        }

        var values = new AuthorityProfile[profileCount];
        var revisions = new HashSet<AuthorityProfileReference>();
        for (var index = 0; index < profileCount; index++)
        {
            var profile = profiles[index];
            values[index] = profile;
            errors.AddRange(AuthorityProfileValidator.Validate(profile).Errors);
            if (profile?.ProfileId is not null && profile.Revision is not null && !revisions.Add(new AuthorityProfileReference(profile.ProfileId, profile.Revision)))
            {
                errors.Add(new AuthorityContractError(AuthorityContractErrorCode.DuplicateProfileRevision, AuthorityContractField.Profiles));
            }
        }

        snapshot = Array.AsReadOnly(values);
        validation = new AuthorityContractValidationResult(errors.Distinct().ToArray());
        return validation.IsValid;
    }
}
