namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects one bounded structured graph or lifecycle validation error.</summary>
/// <param name="Code">The stable machine-readable code.</param>
/// <param name="ElementKind">The exact element kind, or lifecycle for a lifecycle field.</param>
/// <param name="ElementId">The stable element identity when available.</param>
/// <param name="Path">The bounded schema-relative field path.</param>
/// <param name="Message">The bounded value-free explanation when supplied by canonical graph validation.</param>
public sealed record GovernedLoopElementErrorSnapshot(
    string Code,
    string ElementKind,
    string? ElementId,
    string Path,
    string? Message);
