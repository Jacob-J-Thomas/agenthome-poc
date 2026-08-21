using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests append-only retention of provider usage or explicit unavailable posture.</summary>
public sealed record GovernedModelUsageObservationRequest(GovernedModelUsageLedgerIdentity Identity, string ReservationEntryHash, LlmInferenceUsageEvidence Usage, string ProviderEvidenceHash);
