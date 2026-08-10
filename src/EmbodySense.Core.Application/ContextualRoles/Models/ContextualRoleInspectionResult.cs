namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Returns the outcome of validating one exact role revision and its registered source.</summary>
/// <param name="Status">The closed inspection outcome.</param>
/// <param name="Entry">The current safe posture when exact current evidence was proved.</param>
public sealed record ContextualRoleInspectionResult(ContextualRoleInspectionStatus Status, ContextualRoleInspectionEntry? Entry);
