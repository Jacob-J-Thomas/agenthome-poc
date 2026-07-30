namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies one structured authoring validation failure.
/// </summary>
/// <param name="Code">The stable machine-readable validation code.</param>
/// <param name="Field">The request field or element path that failed validation.</param>
/// <param name="Message">The user-facing validation explanation.</param>
public sealed record LoopValidationError(string Code, string Field, string Message);
