namespace EmbodySense.Core.Application.Governance.Tools.Models;

/// <summary>Captures the authority disposition returned after a bounded tool-actuator continuation.</summary>
/// <param name="Disposition">The terminal authority disposition.</param>
/// <param name="Detail">A bounded human-readable explanation.</param>
/// <param name="AuditMetadata">Non-secret evidence attached to the authority audit event.</param>
public sealed record ToolActuationAuthorityExecution(
    ToolActuationAuthorityDisposition Disposition,
    string Detail,
    IReadOnlyDictionary<string, object?> AuditMetadata);
