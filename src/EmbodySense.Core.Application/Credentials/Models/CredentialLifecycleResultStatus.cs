namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Describes one structured value-free lifecycle outcome.</summary>
public enum CredentialLifecycleResultStatus
{
    /// <summary>The requested transition was committed.</summary>
    Applied = 1,
    /// <summary>The exact operation was replayed from durable evidence.</summary>
    Replayed = 2,
    /// <summary>An optimistic revision, operation identity, or preview conflicted.</summary>
    Conflict = 3,
    /// <summary>The request shape or transition was invalid.</summary>
    Invalid = 4,
    /// <summary>The authenticated actor was not authorized for the transition.</summary>
    Denied = 5,
    /// <summary>The target was not found.</summary>
    NotFound = 6,
    /// <summary>The provider or persistence boundary proved a terminal failure.</summary>
    Failed = 7,
    /// <summary>The provider or registry outcome is ambiguous and requires explicit repair.</summary>
    NeedsRepair = 8,
    /// <summary>A required trustworthy dependency is unavailable.</summary>
    Unavailable = 9
}
