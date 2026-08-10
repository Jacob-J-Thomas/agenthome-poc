namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Describes one bounded deterministic Human Input request lifecycle validation error.</summary>
/// <param name="Code">The stable machine-readable failure code.</param>
/// <param name="Path">The bounded schema-relative field path.</param>
/// <param name="Message">The bounded value-free failure explanation.</param>
public sealed record HumanInputRequestLifecycleValidationError(HumanInputRequestLifecycleValidationErrorCode Code, string Path, string Message);
