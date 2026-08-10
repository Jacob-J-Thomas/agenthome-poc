namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Describes one bounded, value-free governed-loop revision contract rejection.</summary>
/// <param name="Code">The closed rejection category.</param>
/// <param name="Path">The safe schema-relative field path.</param>
/// <param name="Message">The fixed value-free rejection message.</param>
public sealed record GovernedLoopRevisionValidationError(
    GovernedLoopRevisionValidationErrorCode Code,
    string Path,
    string Message);
