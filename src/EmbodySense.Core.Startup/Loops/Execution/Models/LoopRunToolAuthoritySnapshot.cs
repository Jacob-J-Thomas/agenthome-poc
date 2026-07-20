namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunToolAuthoritySnapshot(
    string RoleId,
    IReadOnlyList<string> AdmittedMaximum,
    IReadOnlyList<string> CurrentRoleCeiling,
    IReadOnlyList<string> ImplementedCatalog,
    IReadOnlyList<string> EffectiveAssignments,
    string RoleCeilingHash,
    string CatalogHash,
    DateTimeOffset EvaluatedAtUtc,
    bool IsValid,
    string Detail);
