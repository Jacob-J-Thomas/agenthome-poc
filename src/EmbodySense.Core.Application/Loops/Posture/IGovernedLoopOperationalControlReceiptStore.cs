using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Persists bounded idempotent operational-control intent and progress receipts.</summary>
public interface IGovernedLoopOperationalControlReceiptStore
{
    /// <summary>Creates or replays the exact pending receipt before any target mutation.</summary>
    Task<GovernedLoopOperationalControlReceiptStoreResult> BeginAsync(
        GovernedLoopOperationalControlReceipt receipt,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one exact receipt successor through content-hash compare-and-swap.</summary>
    Task<GovernedLoopOperationalControlReceiptStoreResult> CompareExchangeAsync(
        string expectedContentHash,
        GovernedLoopOperationalControlReceipt replacement,
        CancellationToken cancellationToken = default);
}
