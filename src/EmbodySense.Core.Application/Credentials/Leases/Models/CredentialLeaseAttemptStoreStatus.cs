namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Defines the closed outcomes of durable credential-lease attempt operations.</summary>
public enum CredentialLeaseAttemptStoreStatus
{
    /// <summary>A new immutable version was committed.</summary>
    Created = 1,
    /// <summary>The exact durable attempt was replayed.</summary>
    Replayed = 2,
    /// <summary>The stable identity, immutable intent, expected head, successor, or owner conflicted.</summary>
    Conflict = 3,
    /// <summary>Another executor owns the unfinished attempt.</summary>
    OperationInProgress = 4,
    /// <summary>A finite retained-artifact or byte quota prevented publication.</summary>
    Backpressured = 5,
    /// <summary>Retained evidence is malformed, forked, disconnected, or unsafe.</summary>
    Corrupt = 6,
    /// <summary>The durable evidence source is unavailable.</summary>
    Unavailable = 7,
    /// <summary>No exact durable attempt exists.</summary>
    NotFound = 8,
}
