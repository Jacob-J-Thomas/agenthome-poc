namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one bounded, value-free lifecycle request validation error.</summary>
/// <param name="Code">The closed rejection code.</param>
/// <param name="Path">The bounded schema-relative field path.</param>
public sealed record GovernedLoopRevisionLifecycleValidationError(
    GovernedLoopRevisionLifecycleValidationErrorCode Code,
    string Path);
