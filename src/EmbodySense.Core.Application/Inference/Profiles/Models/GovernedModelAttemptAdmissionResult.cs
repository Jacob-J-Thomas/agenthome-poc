using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns exact primary-only attempt eligibility and durable reservation evidence.</summary>
/// <param name="Status">The structured status.</param>
/// <param name="Primary">The exact admitted primary when reserved.</param>
/// <param name="ReservationEntry">The durable reservation entry when reserved or replayed.</param>
/// <param name="CurrentEntry">The authenticated current ledger entry, including an already-advanced phase.</param>
/// <param name="ProviderDispatchMayHaveOccurred">Whether the authenticated history contains the irreversible provider boundary.</param>
public sealed record GovernedModelAttemptAdmissionResult(
    GovernedModelAttemptAdmissionStatus Status,
    GovernedModelProfilePin? Primary,
    GovernedModelUsageLedgerEntry? ReservationEntry,
    GovernedModelUsageLedgerEntry? CurrentEntry = null,
    bool ProviderDispatchMayHaveOccurred = false);
