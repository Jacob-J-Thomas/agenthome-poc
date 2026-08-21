using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns one authenticated durable usage transition.</summary>
public sealed record GovernedModelUsageTransitionResult(GovernedModelUsageTransitionStatus Status, GovernedModelUsageLedgerEntry? Entry);
