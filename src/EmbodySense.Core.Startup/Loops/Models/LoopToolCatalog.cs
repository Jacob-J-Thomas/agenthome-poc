namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Reports the assignments available for custom definitions and their structural authority ceiling.
/// </summary>
/// <param name="CustomAssignable">Role-derived assignments available to the authoring interface.</param>
/// <param name="CustomAuthorityCeiling">The maximum class of authority available to custom loops.</param>
public sealed record LoopToolCatalog(
    IReadOnlyList<LoopToolAssignment> CustomAssignable,
    LoopCustomToolAuthorityCeiling CustomAuthorityCeiling);
