namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns the structured optimistic ledger append outcome.</summary>
/// <param name="Status">The append status.</param>
/// <param name="Generation">The resulting nonnegative generation.</param>
public sealed record GovernedModelUsageLedgerAppendResult(GovernedModelUsageLedgerAppendStatus Status, long Generation);
