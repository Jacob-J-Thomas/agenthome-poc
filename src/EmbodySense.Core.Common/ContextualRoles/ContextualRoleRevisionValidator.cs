using EmbodySense.Core.Common.ContextualRoles.Models;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Validates bounded, immutable contextual-role revision contracts without loading sources or granting authority.</summary>
public static class ContextualRoleRevisionValidator
{
    /// <summary>Validates a revision and returns all deterministic structured errors.</summary>
    /// <param name="revision">The candidate immutable revision.</param>
    /// <returns>The complete validation result.</returns>
    public static ContextualRoleValidationResult Validate(ContextualRoleRevision? revision)
    {
        var errors = new List<ContextualRoleValidationError>();
        if (revision is null)
        {
            Add(errors, "revision_required", "$", "Contextual-role revision is required.");
            return Result(errors);
        }

        if (revision.SchemaVersion != ContextualRoleLimits.SchemaVersion)
        {
            Add(errors, "unsupported_schema_version", "schemaVersion", "Schema version must be 1.");
        }

        ValidateIdentity(revision.Identity, errors);
        ValidateText(revision.DisplayName, "displayName", ContextualRoleLimits.MaxDisplayNameCharacters, required: true, errors);
        ValidateText(revision.Purpose, "purpose", ContextualRoleLimits.MaxPurposeCharacters, required: true, errors);
        ValidateStatus(revision.Status, errors);
        ValidateProvenance(revision.Provenance, errors);
        ValidateWorkspaceApplicability(revision.WorkspaceApplicability, errors);
        ValidateInstructionSource(revision.InstructionSource, errors);
        ValidatePolicyMaxima(revision.PolicyMaxima, errors);
        if (!IsSha256Hex(revision.ContentHash))
        {
            Add(errors, "invalid_content_hash", "contentHash", "Content hash must be a 64-character lowercase SHA-256 hexadecimal value.");
        }
        else if (CanVerifyContentHash(revision) && !ContextualRoleRevisionContentHash.Matches(revision))
        {
            Add(errors, "content_hash_mismatch", "contentHash", "Content hash does not match the canonical semantic revision content.");
        }

        return Result(errors);
    }

    private static ContextualRoleValidationResult Result(IReadOnlyList<ContextualRoleValidationError> errors)
        => new(errors, errors.Count == 0);

    private static void ValidateIdentity(ContextualRoleRevisionIdentity? identity, List<ContextualRoleValidationError> errors)
    {
        if (identity is null)
        {
            Add(errors, "identity_required", "identity", "Immutable role revision identity is required.");
            return;
        }

        if (!ContextualRoleId.IsValid(identity.RoleId))
        {
            Add(errors, "invalid_role_id", "identity.roleId", "Role id must be a bounded lowercase ASCII identifier.");
        }

        if (identity.Revision < 1)
        {
            Add(errors, "invalid_revision", "identity.revision", "Revision must be at least 1.");
        }
    }

    private static void ValidateStatus(ContextualRoleStatus status, List<ContextualRoleValidationError> errors)
    {
        if (!Enum.IsDefined(status) || status == ContextualRoleStatus.Unknown)
        {
            Add(errors, "invalid_status", "status", "Status must be draft, published, disabled, archived, or replaced.");
        }
    }

    private static void ValidateProvenance(ContextualRoleProvenance? provenance, List<ContextualRoleValidationError> errors)
    {
        if (provenance is null)
        {
            Add(errors, "provenance_required", "provenance", "Provenance is required.");
            return;
        }

        if (!ContextualRoleId.IsValid(provenance.AuthorId))
        {
            Add(errors, "invalid_author_id", "provenance.authorId", "Author id must be a bounded lowercase ASCII identifier.");
        }

        if (!IsUtcTimestamp(provenance.CreatedAtUtc))
        {
            Add(errors, "invalid_created_timestamp", "provenance.createdAtUtc", "Created timestamp must be non-default UTC.");
        }

        if (!IsUtcTimestamp(provenance.RecordedAtUtc))
        {
            Add(errors, "invalid_recorded_timestamp", "provenance.recordedAtUtc", "Recorded timestamp must be non-default UTC.");
        }

        if (provenance.CreatedAtUtc > provenance.RecordedAtUtc)
        {
            Add(errors, "invalid_provenance_timestamp_order", "provenance.recordedAtUtc", "Recorded timestamp cannot precede the created timestamp.");
        }
    }

    private static void ValidateWorkspaceApplicability(ContextualRoleWorkspaceApplicability? applicability, List<ContextualRoleValidationError> errors)
    {
        if (applicability is null || applicability.WorkspaceIds.IsDefault)
        {
            Add(errors, "workspace_applicability_required", "workspaceApplicability", "An initialized explicit workspace scope is required.");
            return;
        }

        if (applicability.WorkspaceIds.Length == 0 || applicability.WorkspaceIds.Length > ContextualRoleLimits.MaxWorkspaceScopes)
        {
            Add(errors, "workspace_scope_count_out_of_range", "workspaceApplicability.workspaceIds", $"Workspace scope count must be between 1 and {ContextualRoleLimits.MaxWorkspaceScopes}.");
        }

        ValidateIdentifiers(applicability.WorkspaceIds, "workspaceApplicability.workspaceIds", "invalid_workspace_id", "duplicate_workspace_id", errors);
    }

    private static void ValidateInstructionSource(ContextualRoleInstructionSourceReference? source, List<ContextualRoleValidationError> errors)
    {
        if (source is null)
        {
            Add(errors, "instruction_source_required", "instructionSource", "A classified instruction source reference is required.");
            return;
        }

        if (!Enum.IsDefined(source.Kind) || source.Kind == ContextualRoleInstructionSourceKind.Unknown)
        {
            Add(errors, "invalid_instruction_source_kind", "instructionSource.kind", "Instruction source must use a registered role-source convention.");
        }

        if (source.Classification != ContextualRoleInstructionClassification.RoleInstruction)
        {
            Add(errors, "untrusted_instruction_source", "instructionSource.classification", "Only explicitly classified role instructions may be referenced by a contextual role.");
        }

        if (!ContextualRoleId.IsValid(source.ReferenceId) || source.ReferenceId.Length > ContextualRoleLimits.MaxInstructionSourceReferenceCharacters)
        {
            Add(errors, "invalid_instruction_source_reference", "instructionSource.referenceId", "Instruction source reference must be a bounded lowercase ASCII opaque identifier.");
        }
    }

    private static void ValidatePolicyMaxima(ContextualRolePolicyMaxima? maxima, List<ContextualRoleValidationError> errors)
    {
        if (maxima is null || maxima.CapabilityIds.IsDefault)
        {
            Add(errors, "policy_maxima_required", "policyMaxima", "Initialized non-granting policy maxima are required.");
            return;
        }

        if (maxima.CapabilityIds.Length > ContextualRoleLimits.MaxCapabilityMaximums)
        {
            Add(errors, "capability_maximum_count_out_of_range", "policyMaxima.capabilityIds", $"Capability maximum count cannot exceed {ContextualRoleLimits.MaxCapabilityMaximums}.");
        }

        ValidateIdentifiers(maxima.CapabilityIds, "policyMaxima.capabilityIds", "invalid_capability_maximum", "duplicate_capability_maximum", errors);
    }

    private static void ValidateIdentifiers(ImmutableArray<string> identifiers, string field, string invalidCode, string duplicateCode, List<ContextualRoleValidationError> errors)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < identifiers.Length; index++)
        {
            if (!ContextualRoleId.IsValid(identifiers[index]))
            {
                Add(errors, invalidCode, $"{field}[{index}]", "Identifier must be a bounded lowercase ASCII identifier.");
            }
            else if (!unique.Add(identifiers[index]))
            {
                Add(errors, duplicateCode, $"{field}[{index}]", "Identifiers must be unique using ordinal comparison.");
            }
        }
    }

    private static void ValidateText(string? value, string field, int maximumLength, bool required, List<ContextualRoleValidationError> errors)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            Add(errors, $"{field}_required", field, $"{field} is required.");
            return;
        }

        if (value.Length > maximumLength)
        {
            Add(errors, $"{field}_too_long", field, $"{field} cannot exceed {maximumLength} characters.");
        }

        if (ContainsUnsafeCharacters(value) || !value.IsNormalized(NormalizationForm.FormC))
        {
            Add(errors, "unsafe_text_characters", field, $"{field} contains unsupported Unicode or control characters.");
        }
    }

    private static bool IsSha256Hex(string? value) => value is { Length: ContextualRoleLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CanVerifyContentHash(ContextualRoleRevision revision)
    {
        return IsWellFormedUtf16(revision.Identity?.RoleId)
            && IsWellFormedUtf16(revision.Purpose)
            && IsWellFormedUtf16(revision.InstructionSource?.ReferenceId)
            && (revision.WorkspaceApplicability?.WorkspaceIds ?? []).All(IsWellFormedUtf16)
            && (revision.PolicyMaxima?.CapabilityIds ?? []).All(IsWellFormedUtf16);
    }

    private static bool IsUtcTimestamp(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsWellFormedUtf16(string? value)
    {
        if (value is null)
        {
            return true;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUnsafeCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                if (CharUnicodeInfo.GetUnicodeCategory(value, index) == UnicodeCategory.Format)
                {
                    return true;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character)
                || char.IsControl(character) && character is not '\r' and not '\n' and not '\t'
                || CharUnicodeInfo.GetUnicodeCategory(value, index) == UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(List<ContextualRoleValidationError> errors, string code, string field, string message) => errors.Add(new ContextualRoleValidationError(code, field, message));
}
