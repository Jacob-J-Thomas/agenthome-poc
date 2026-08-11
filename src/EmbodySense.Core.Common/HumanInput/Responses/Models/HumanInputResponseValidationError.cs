namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Describes one bounded value-free authenticated-response contract violation.</summary>
/// <param name="Code">The stable machine-readable validation code.</param>
/// <param name="Path">The bounded schema-relative field path.</param>
/// <param name="Message">The bounded value-free failure explanation.</param>
public sealed record HumanInputResponseValidationError(HumanInputResponseValidationErrorCode Code, string Path, string Message);
