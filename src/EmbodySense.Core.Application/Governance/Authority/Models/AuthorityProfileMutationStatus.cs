namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Identifies the durable outcome of an authority-profile mutation.</summary>
public enum AuthorityProfileMutationStatus
{
    /// <summary>The requested state and receipt were committed.</summary>
    Applied = 1,
    /// <summary>The exact request was recovered from immutable operation evidence.</summary>
    Replayed = 2,
    /// <summary>The operation id or expected profile revision conflicts with durable state.</summary>
    Conflict = 3,
    /// <summary>The profile does not exist.</summary>
    NotFound = 4,
    /// <summary>The request is outside the bounded schema-1 contract.</summary>
    Invalid = 5,
    /// <summary>The store cannot establish a trustworthy mutation base or outcome.</summary>
    Unavailable = 6
}
