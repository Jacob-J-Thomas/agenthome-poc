namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Describes one bounded value-free response-command validation failure.</summary>
/// <param name="Code">The stable machine-readable code.</param>
/// <param name="Path">The schema-relative field path.</param>
/// <param name="Message">The value-free failure explanation.</param>
public sealed record HumanInputResponseLifecycleMutationValidationError(
    HumanInputResponseLifecycleMutationValidationErrorCode Code,
    string Path,
    string Message);
