namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool request.
/// </summary>
/// <param name="Command">The command.</param>
/// <param name="TargetPath">The target path.</param>
/// <param name="Content">The exact content.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="CorrelationId">The correlation ID.</param>
/// <param name="AuditCorrelation">The audit correlation.</param>
public sealed record ToolRequest(
    ToolCommand Command,
    string TargetPath,
    string? Content = null,
    string? Pattern = null,
    string? CorrelationId = null,
    ToolAuditCorrelation? AuditCorrelation = null);
