namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>References one classified instruction source without interpreting repository text or granting trust.</summary>
/// <param name="Kind">The registered source convention.</param>
/// <param name="ReferenceId">A bounded opaque source identifier, not a filesystem path.</param>
/// <param name="Classification">The source classification, which must remain role instruction material.</param>
public sealed record ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind Kind, string ReferenceId, ContextualRoleInstructionClassification Classification);
