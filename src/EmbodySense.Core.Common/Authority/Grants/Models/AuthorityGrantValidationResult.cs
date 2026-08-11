namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Returns a bounded defensive snapshot of authority-grant validation failures.</summary>
/// <param name="Errors">The bounded value-free validation failures.</param>
/// <param name="IsValid">Whether the complete contract is valid.</param>
public sealed record AuthorityGrantValidationResult(IReadOnlyList<AuthorityGrantValidationError> Errors, bool IsValid);
