namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopToolCatalog(
    IReadOnlyList<LoopToolAssignment> CustomAssignable,
    LoopCustomToolAuthorityCeiling CustomAuthorityCeiling);
