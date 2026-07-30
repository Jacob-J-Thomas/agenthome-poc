namespace EmbodySense.Core.Application.Governance.Tools.Models;

/// <summary>
/// Captures the dynamic authority decision made immediately before tool actuation.
/// </summary>
/// <param name="Allowed">Whether the request remains authorized.</param>
/// <param name="Detail">A human-readable explanation of the decision.</param>
/// <param name="AuditMetadata">Evidence to attach to the authority audit event.</param>
public sealed record ToolActuationAuthorityRevalidation(
    bool Allowed,
    string Detail,
    IReadOnlyDictionary<string, object?> AuditMetadata);
