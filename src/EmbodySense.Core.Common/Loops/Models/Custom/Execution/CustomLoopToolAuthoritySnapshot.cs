namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopToolAuthoritySnapshot(
    string RoleId,
    CustomLoopToolAssignment[] AdmittedMaximum,
    CustomLoopToolAssignment[] CurrentRoleCeiling,
    CustomLoopToolAssignment[] ImplementedCatalog,
    CustomLoopToolAssignment[] EffectiveAssignments,
    string RoleCeilingHash,
    string CatalogHash,
    DateTimeOffset EvaluatedAtUtc,
    bool IsValid,
    string Detail);
