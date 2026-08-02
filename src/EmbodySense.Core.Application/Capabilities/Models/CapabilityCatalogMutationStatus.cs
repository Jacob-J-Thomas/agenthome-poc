namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies the durable outcome of a catalog mutation.</summary>
public enum CapabilityCatalogMutationStatus
{
    /// <summary>The requested lifecycle state was committed.</summary>
    Applied = 1,
    /// <summary>The operation was durably recorded but lifecycle state was already identical.</summary>
    NoChange = 2,
    /// <summary>The exact operation was replayed from its durable receipt.</summary>
    Replayed = 3,
    /// <summary>The optimistic catalog revision was stale.</summary>
    Conflict = 4,
    /// <summary>The target capability was not found.</summary>
    NotFound = 5,
    /// <summary>The transition request or lifecycle transition was invalid.</summary>
    Invalid = 6,
    /// <summary>The store could not establish a trustworthy mutation base or durable outcome.</summary>
    Unavailable = 7
}
