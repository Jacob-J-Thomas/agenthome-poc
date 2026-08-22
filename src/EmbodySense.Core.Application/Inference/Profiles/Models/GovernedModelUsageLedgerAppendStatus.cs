namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies an optimistic model-usage ledger append outcome.</summary>
public enum GovernedModelUsageLedgerAppendStatus
{
    /// <summary>The exact entry was appended.</summary>
    Appended = 1,
    /// <summary>The identical entry already exists.</summary>
    AlreadyPresent = 2,
    /// <summary>Generation or operation content conflicts.</summary>
    Conflict = 3,
    /// <summary>The atomic reservation would exceed its node-series or run-wide hard ceiling.</summary>
    BudgetExhausted = 4,
    /// <summary>Durable state is unavailable or ambiguous.</summary>
    Unavailable = 5
}
