using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns the exact authenticated server-derived reservation and append posture.</summary>
public sealed record GovernedModelUsageReservationResult(
    GovernedModelUsageLedgerAppendStatus Status,
    long Generation,
    GovernedModelUsageLedgerEntry? ReservationEntry);
