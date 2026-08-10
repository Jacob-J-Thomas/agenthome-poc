namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Describes one bounded value-free Human Input lifecycle command validation failure.</summary>
/// <param name="Code">The stable validation code.</param>
/// <param name="Path">The bounded structural path.</param>
/// <param name="Message">The bounded value-free explanation.</param>
public sealed record HumanInputRequestLifecycleMutationValidationError(
    HumanInputRequestLifecycleMutationValidationErrorCode Code,
    string Path,
    string Message);
