using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests atomic derivation and retention of one effective reservation from current aggregate posture.</summary>
public sealed record GovernedModelUsageReservationRequest(
    GovernedModelUsageLedgerIdentity Identity,
    GovernedModelBudgetPolicy BudgetPolicy,
    string EvidenceHash,
    DateTimeOffset RecordedAtUtc);
