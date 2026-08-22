using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests one append-only dispatch-boundary or affirmative-not-started proof.</summary>
public sealed record GovernedModelDispatchEvidenceRequest(GovernedModelUsageLedgerIdentity Identity, string ReservationEntryHash, bool DispatchStarted, string DispatchEvidenceHash);
