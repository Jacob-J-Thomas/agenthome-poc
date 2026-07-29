using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools.Models;

internal sealed record ToolTerminalOutcome(
    ToolExecutionOutcome Outcome,
    string Detail,
    ToolGovernanceEvidence GovernanceEvidence,
    string AuditOutcome,
    IReadOnlyDictionary<string, object?> AuditMetadata);
