namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Projects one bounded value-free malformed-operation error.</summary>
/// <param name="Code">The stable machine-readable validation code.</param>
/// <param name="Path">The bounded structural field path.</param>
/// <param name="Message">The bounded value-free validation explanation.</param>
public sealed record HumanInputOperationValidationError(string Code, string Path, string Message);
