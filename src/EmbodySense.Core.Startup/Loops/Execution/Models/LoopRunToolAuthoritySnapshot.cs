namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects admitted, current, catalog, and effective custom-loop tool authority with integrity hashes.
/// </summary>
/// <param name="RoleId">The role identifier.</param>
/// <param name="AdmittedMaximum">The admitted maximum.</param>
/// <param name="CurrentRoleCeiling">The current role ceiling.</param>
/// <param name="ImplementedCatalog">The implemented catalog.</param>
/// <param name="EffectiveAssignments">The effective assignments.</param>
/// <param name="RoleCeilingHash">The role ceiling hash.</param>
/// <param name="CatalogHash">The catalog hash.</param>
/// <param name="EvaluatedAtUtc">The evaluated at utc.</param>
/// <param name="IsValid">The is valid.</param>
/// <param name="Detail">The detail.</param>
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
