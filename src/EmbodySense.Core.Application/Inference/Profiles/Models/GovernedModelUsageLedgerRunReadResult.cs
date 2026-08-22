using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns the bounded authenticated provider-usage histories associated with one exact run.</summary>
/// <param name="Status">The authenticated read status.</param>
/// <param name="Entries">Canonical workspace-append order entries belonging only to the requested run.</param>
/// <param name="WorkspaceGeneration">The nonnegative global generation of the authenticated segmented workspace ledger.</param>
public sealed record GovernedModelUsageLedgerRunReadResult(
    GovernedModelUsageLedgerReadStatus Status,
    IReadOnlyList<GovernedModelUsageLedgerEntry> Entries,
    long WorkspaceGeneration);
