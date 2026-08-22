using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns exact append-only provider-usage history.</summary>
/// <param name="Status">The read status.</param>
/// <param name="Entries">Canonical entries ordered by generation.</param>
/// <param name="Generation">The nonnegative current generation.</param>
public sealed record GovernedModelUsageLedgerReadResult(GovernedModelUsageLedgerReadStatus Status, IReadOnlyList<GovernedModelUsageLedgerEntry> Entries, long Generation);
