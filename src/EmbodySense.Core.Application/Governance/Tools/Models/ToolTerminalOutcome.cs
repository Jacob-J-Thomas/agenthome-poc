using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools.Models;

/// <summary>
/// Carries a non-actuated terminal result together with governance and audit evidence.
/// </summary>
/// <param name="Outcome">The model-facing execution outcome.</param>
/// <param name="Detail">The model-facing outcome explanation.</param>
/// <param name="GovernanceEvidence">The authority, permission, and approval decisions.</param>
/// <param name="AuditOutcome">The canonical audit outcome name.</param>
/// <param name="AuditMetadata">Additional evidence for the terminal audit event.</param>
internal sealed record ToolTerminalOutcome(
    ToolExecutionOutcome Outcome,
    string Detail,
    ToolGovernanceEvidence GovernanceEvidence,
    string AuditOutcome,
    IReadOnlyDictionary<string, object?> AuditMetadata);
