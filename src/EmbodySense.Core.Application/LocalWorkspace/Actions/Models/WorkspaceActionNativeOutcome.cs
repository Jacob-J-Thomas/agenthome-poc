namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Returns the value-free evidence references for one conclusive successful native commit.</summary>
/// <param name="OutcomeEvidenceId">The exact bounded outcome-evidence reference.</param>
/// <param name="AfterEvidenceId">The exact bounded after-state evidence reference.</param>
public sealed record WorkspaceActionNativeOutcome(
    string OutcomeEvidenceId,
    string AfterEvidenceId);
