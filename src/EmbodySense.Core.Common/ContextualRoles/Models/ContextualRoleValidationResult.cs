namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Represents the complete structured result of validating a contextual-role revision.</summary>
/// <param name="Errors">The deterministic validation errors.</param>
public sealed record ContextualRoleValidationResult(IReadOnlyList<ContextualRoleValidationError> Errors)
{
    /// <summary>Gets a value indicating whether the revision satisfies every contract invariant.</summary>
    public bool IsValid => Errors.Count == 0;
}
