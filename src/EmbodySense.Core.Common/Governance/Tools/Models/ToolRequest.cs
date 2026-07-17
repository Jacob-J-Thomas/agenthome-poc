namespace EmbodySense.Core.Common.Governance.Tools.Models;

public sealed record ToolRequest(
    ToolCommand Command,
    string TargetPath,
    string? Content = null,
    string? Pattern = null,
    string? CorrelationId = null,
    ToolAuditCorrelation? AuditCorrelation = null);

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
