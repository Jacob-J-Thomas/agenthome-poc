namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Describes one deterministic contextual-role contract validation failure.</summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Field">The field whose value failed validation.</param>
/// <param name="Message">The human-readable validation explanation.</param>
public sealed record ContextualRoleValidationError(string Code, string Field, string Message);
