namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Represents one bounded value-free mutation-request error.</summary>
/// <param name="Code">The closed error code.</param>
/// <param name="Path">The bounded public request path.</param>
public sealed record AuthorityGrantMutationValidationError(AuthorityGrantMutationValidationErrorCode Code, string Path);
