namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop validation error.
/// </summary>
/// <param name="Code">The code.</param>
/// <param name="Field">The field.</param>
/// <param name="Message">The message.</param>
public sealed record CustomLoopValidationError(string Code, string Field, string Message);
