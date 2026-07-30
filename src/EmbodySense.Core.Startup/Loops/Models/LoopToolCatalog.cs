namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopToolCatalog(
    IReadOnlyList<LoopToolAssignment> CustomAssignable,
    LoopCustomToolAuthorityCeiling CustomAuthorityCeiling);
