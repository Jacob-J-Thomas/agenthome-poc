namespace EmbodySense.Core.Application.Governance.Tools.Models;

public sealed record ToolActuationAuthorityRevalidation(
    bool Allowed,
    string Detail,
    IReadOnlyDictionary<string, object?> AuditMetadata);
