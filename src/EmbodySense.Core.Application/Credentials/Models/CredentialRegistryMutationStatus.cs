namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one durable credential-registry mutation outcome.</summary>
public enum CredentialRegistryMutationStatus
{
    /// <summary>The transition was committed.</summary>
    Applied = 1,
    /// <summary>The exact operation was replayed from immutable evidence.</summary>
    Replayed = 2,
    /// <summary>The observed revision or operation intent conflicts with current state.</summary>
    Conflict = 3,
    /// <summary>The target reference does not exist.</summary>
    NotFound = 4,
    /// <summary>The request is structurally invalid.</summary>
    Invalid = 5,
    /// <summary>The store cannot establish a trustworthy state or durable outcome.</summary>
    Unavailable = 6
}
