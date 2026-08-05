using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Validates closed schema-version-1 authority profiles and ceilings without granting or executing authority.
/// </summary>
public static class AuthorityProfileValidator
{
    /// <summary>
    /// Validates a complete authority profile.
    /// </summary>
    /// <param name="profile">The profile to validate.</param>
    /// <returns>The complete structured validation result.</returns>
    public static AuthorityContractValidationResult Validate(AuthorityProfile? profile)
    {
        var errors = new List<AuthorityContractError>();
        if (profile is null)
        {
            Add(errors, AuthorityContractErrorCode.Required, AuthorityContractField.Contract);
            return new AuthorityContractValidationResult(errors);
        }

        if (profile.SchemaVersion != AuthorityProfile.CurrentSchemaVersion)
        {
            Add(errors, AuthorityContractErrorCode.UnsupportedSchemaVersion, AuthorityContractField.SchemaVersion);
        }

        Require(profile.ProfileId, AuthorityContractField.ProfileId, errors);
        Require(profile.Revision, AuthorityContractField.Revision, errors);
        Require(profile.Purpose, AuthorityContractField.Purpose, errors);
        ValidateStatus(profile.Status, errors);
        ValidateProvenance(profile.Provenance, errors);
        ValidateTimestamps(profile.IssuedAtUtc, profile.ExpiresAtUtc, errors);
        errors.AddRange(ValidateCeiling(profile.Ceiling).Errors);
        ValidateConditions(profile.BoundaryConditions, errors);
        return new AuthorityContractValidationResult(errors.Distinct().ToArray());
    }

    /// <summary>
    /// Validates one bounded candidate authority ceiling.
    /// </summary>
    /// <param name="ceiling">The ceiling to validate.</param>
    /// <returns>The complete structured validation result.</returns>
    public static AuthorityContractValidationResult ValidateCeiling(AuthorityCeiling? ceiling)
    {
        var errors = new List<AuthorityContractError>();
        if (ceiling is null)
        {
            Add(errors, AuthorityContractErrorCode.CeilingRequired, AuthorityContractField.Ceiling);
            return new AuthorityContractValidationResult(errors);
        }

        ValidateCapabilities(ceiling.Capabilities, errors);
        ValidateDataClasses(ceiling.DataClasses, errors);
        if (ceiling.MaxTargetCount is < 0 or > AuthorityContractLimits.MaxTargetCount)
        {
            Add(errors, AuthorityContractErrorCode.TargetCountOutOfRange, AuthorityContractField.MaxTargetCount);
        }

        if (!Enum.IsDefined(ceiling.MaxSideEffectClass) || ceiling.MaxSideEffectClass == CapabilitySideEffectClass.Unknown)
        {
            Add(errors, AuthorityContractErrorCode.UnsupportedSideEffectClass, AuthorityContractField.MaxSideEffectClass);
        }

        return new AuthorityContractValidationResult(errors);
    }

    private static void ValidateStatus(AuthorityProfileStatus status, List<AuthorityContractError> errors)
    {
        if (!Enum.IsDefined(status) || status == AuthorityProfileStatus.Unknown)
        {
            Add(errors, AuthorityContractErrorCode.UnsupportedStatus, AuthorityContractField.Status);
        }
    }

    private static void ValidateProvenance(AuthorityProvenance? provenance, List<AuthorityContractError> errors)
    {
        if (provenance is null)
        {
            Add(errors, AuthorityContractErrorCode.Required, AuthorityContractField.Provenance);
            return;
        }

        Require(provenance.ActorId, AuthorityContractField.ProvenanceActorId, errors);
        if (!Enum.IsDefined(provenance.Kind) || provenance.Kind == AuthorityProvenanceKind.Unknown)
        {
            Add(errors, AuthorityContractErrorCode.UnsupportedProvenanceKind, AuthorityContractField.ProvenanceKind);
        }
    }

    private static void ValidateTimestamps(DateTimeOffset issuedAtUtc, DateTimeOffset? expiresAtUtc, List<AuthorityContractError> errors)
    {
        if (issuedAtUtc.Offset != TimeSpan.Zero)
        {
            Add(errors, AuthorityContractErrorCode.InvalidTimestamp, AuthorityContractField.IssuedAtUtc);
        }

        if (expiresAtUtc is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry <= issuedAtUtc))
        {
            Add(errors, AuthorityContractErrorCode.InvalidTimestamp, AuthorityContractField.ExpiresAtUtc);
        }
    }

    private static void ValidateCapabilities(IReadOnlyList<CapabilityDescriptorIdentity>? capabilities, List<AuthorityContractError> errors)
    {
        if (capabilities is null || capabilities.Count > AuthorityContractLimits.MaxCapabilitiesPerCeiling)
        {
            Add(errors, AuthorityContractErrorCode.CollectionOutOfRange, AuthorityContractField.Capabilities);
            return;
        }

        var seen = new HashSet<CapabilityDescriptorIdentity>();
        foreach (var capability in capabilities)
        {
            if (capability?.Id is null || capability.Version is null || capability.Hash is null)
            {
                Add(errors, AuthorityContractErrorCode.CapabilityIdentityRequired, AuthorityContractField.Capabilities);
            }
            else if (!seen.Add(capability))
            {
                Add(errors, AuthorityContractErrorCode.DuplicateCollectionItem, AuthorityContractField.Capabilities);
            }
        }
    }

    private static void ValidateDataClasses(IReadOnlyList<CapabilityDataClass>? dataClasses, List<AuthorityContractError> errors)
    {
        if (dataClasses is null || dataClasses.Count > AuthorityContractLimits.MaxDataClassesPerCeiling)
        {
            Add(errors, AuthorityContractErrorCode.CollectionOutOfRange, AuthorityContractField.DataClasses);
            return;
        }

        var seen = new HashSet<CapabilityDataClass>();
        foreach (var dataClass in dataClasses)
        {
            if (dataClass is null)
            {
                Add(errors, AuthorityContractErrorCode.CollectionItemRequired, AuthorityContractField.DataClasses);
            }
            else if (!seen.Add(dataClass))
            {
                Add(errors, AuthorityContractErrorCode.DuplicateCollectionItem, AuthorityContractField.DataClasses);
            }
        }
    }

    private static void ValidateConditions(IReadOnlyList<AuthorityBoundaryCondition>? conditions, List<AuthorityContractError> errors)
    {
        if (conditions is null || conditions.Count > AuthorityContractLimits.MaxBoundaryConditionsPerProfile)
        {
            Add(errors, AuthorityContractErrorCode.CollectionOutOfRange, AuthorityContractField.BoundaryConditions);
            return;
        }

        var seen = new HashSet<AuthorityBoundaryCondition>();
        foreach (var condition in conditions)
        {
            var error = AuthorityBoundaryConditionValidator.Validate(condition);
            if (error is not null)
            {
                errors.Add(error);
            }
            else if (!seen.Add(condition!))
            {
                Add(errors, AuthorityContractErrorCode.DuplicateCollectionItem, AuthorityContractField.BoundaryConditions);
            }
        }
    }

    private static void Require<T>(T? value, AuthorityContractField field, List<AuthorityContractError> errors) where T : class
    {
        if (value is null)
        {
            Add(errors, AuthorityContractErrorCode.Required, field);
        }
    }

    private static void Add(List<AuthorityContractError> errors, AuthorityContractErrorCode code, AuthorityContractField field)
    {
        errors.Add(new AuthorityContractError(code, field));
    }
}
