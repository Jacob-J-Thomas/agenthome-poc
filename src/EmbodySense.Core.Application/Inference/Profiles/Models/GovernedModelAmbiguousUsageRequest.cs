using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests durable attention posture when provider transport may have started but no trustworthy usage outcome exists.</summary>
public sealed record GovernedModelAmbiguousUsageRequest(GovernedModelUsageLedgerIdentity Identity, string ReservationEntryHash, string AmbiguityEvidenceHash);
