namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies a model-usage ledger read result.</summary>
public enum GovernedModelUsageLedgerReadStatus
{
    /// <summary>Canonical retained history was found.</summary>
    Found = 1,
    /// <summary>No history exists for the exact identity.</summary>
    NotFound = 2,
    /// <summary>Trusted durable state could not be established.</summary>
    Unavailable = 3
}
