using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Validates the closed schema-version-1 server-owned lifecycle snapshot shape.
/// </summary>
public static class CapabilityLifecycleSnapshotValidator
{
    /// <summary>
    /// Validates lifecycle state axes without inferring assignment or authority.
    /// </summary>
    /// <param name="snapshot">The lifecycle snapshot to validate.</param>
    /// <returns>The structured validation result.</returns>
    public static CapabilityContractValidationResult Validate(CapabilityLifecycleSnapshot? snapshot)
    {
        var errors = new List<CapabilityContractError>();
        if (snapshot is null)
        {
            errors.Add(new CapabilityContractError("lifecycle_snapshot_required", "$", "A capability lifecycle snapshot is required."));
            return new CapabilityContractValidationResult(errors);
        }

        if (snapshot.SchemaVersion != CapabilityLifecycleSnapshot.CurrentSchemaVersion)
        {
            errors.Add(new CapabilityContractError("unsupported_schema_version", "schemaVersion", "Only experimental capability lifecycle schema version 1 is supported."));
        }

        if (snapshot.DescriptorIdentity?.Id is null || snapshot.DescriptorIdentity.Version is null || snapshot.DescriptorIdentity.Hash is null)
        {
            errors.Add(new CapabilityContractError("descriptor_identity_required", "descriptorIdentity", "An exact descriptor identity is required."));
        }

        ValidateEnum(snapshot.Declaration, "declaration", errors);
        ValidateEnum(snapshot.Installation, "installation", errors);
        ValidateEnum(snapshot.Enablement, "enablement", errors);
        ValidateEnum(snapshot.Health, "health", errors);
        ValidateEnum(snapshot.Retirement, "retirement", errors);
        ValidateEnum(snapshot.Trust, "trust", errors);
        return new CapabilityContractValidationResult(errors);
    }

    private static void ValidateEnum<T>(T value, string field, List<CapabilityContractError> errors) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add(new CapabilityContractError("unsupported_lifecycle_state", field, "The lifecycle state value is unsupported."));
        }
    }
}
