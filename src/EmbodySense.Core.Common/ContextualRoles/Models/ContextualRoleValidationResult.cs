namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Represents the complete structured result of validating a contextual-role revision.</summary>
/// <param name="Errors">The deterministic validation errors.</param>
/// <param name="IsValid">Whether the revision satisfies every contract invariant.</param>
public sealed record ContextualRoleValidationResult(
    IReadOnlyList<ContextualRoleValidationError> Errors,
    bool IsValid);
