namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Describes one bounded value-free authority-grant validation failure.</summary>
/// <param name="Code">The closed failure classification.</param>
/// <param name="Path">The stable schema field path.</param>
/// <param name="Message">A safe explanation that never includes caller-controlled values.</param>
public sealed record AuthorityGrantValidationError(AuthorityGrantValidationErrorCode Code, string Path, string Message);
