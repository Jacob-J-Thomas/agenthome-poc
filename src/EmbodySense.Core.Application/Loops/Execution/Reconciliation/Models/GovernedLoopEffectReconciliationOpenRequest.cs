using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Supplies a server-composed request to open one exact reconciliation-required effect.</summary>
/// <param name="OperationId">The stable mutation identity.</param>
/// <param name="CaseId">The server-selected bounded case identity.</param>
/// <param name="Binding">The exact current effect binding.</param>
/// <param name="ContractMetadata">The server-composed versioned reconciliation contract.</param>
/// <param name="EvidenceSources">The server-composed ordered evidence-source registrations.</param>
/// <param name="CaseReceiptHashes">The bounded value-free admission receipts associated with the case.</param>
public sealed record GovernedLoopEffectReconciliationOpenRequest(
    string? OperationId,
    string? CaseId,
    GovernedLoopEffectReconciliationBinding? Binding,
    GovernedLoopEffectReconciliationContractMetadata? ContractMetadata,
    IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource>? EvidenceSources,
    IReadOnlyList<string>? CaseReceiptHashes);
