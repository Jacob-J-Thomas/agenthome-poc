using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Persists crash-safe append-only model-usage reservation and reconciliation evidence.</summary>
public interface IGovernedModelUsageLedger
{
    /// <summary>Reads exact provider-attempt history.</summary>
    Task<GovernedModelUsageLedgerReadResult> ReadAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Reads all bounded authenticated histories belonging to one exact workspace/run without exposing entries from another run.</summary>
    Task<GovernedModelUsageLedgerRunReadResult> ReadRunAsync(string workspaceId, string runId, CancellationToken cancellationToken = default);

    /// <summary>Atomically checks per-node-series/run aggregate posture and appends a generation-one reservation.</summary>
    Task<GovernedModelUsageReservationResult> ReserveAsync(GovernedModelUsageReservationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Appends one exact transition with optimistic generation.</summary>
    Task<GovernedModelUsageLedgerAppendResult> AppendAsync(GovernedModelUsageLedgerEntry entry, long expectedGeneration, CancellationToken cancellationToken = default);
}
