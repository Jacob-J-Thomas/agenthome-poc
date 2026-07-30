namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool audit correlation.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step ID.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="AttemptCorrelationId">The attempt correlation ID.</param>
/// <param name="AdmittedCommands">The admitted commands.</param>
/// <param name="CurrentRoleCommands">The current role commands.</param>
/// <param name="EffectiveCommands">The effective commands.</param>
/// <param name="RoleCeilingHash">The role ceiling hash.</param>
/// <param name="CatalogHash">The catalog hash.</param>
public sealed record ToolAuditCorrelation(
    string RunId,
    string LoopId,
    string RoleId,
    int DefinitionVersion,
    string DefinitionHash,
    int Iteration,
    string StepId,
    int Attempt,
    string AttemptCorrelationId,
    string? AdmittedCommands = null,
    string? CurrentRoleCommands = null,
    string? EffectiveCommands = null,
    string? RoleCeilingHash = null,
    string? CatalogHash = null);
