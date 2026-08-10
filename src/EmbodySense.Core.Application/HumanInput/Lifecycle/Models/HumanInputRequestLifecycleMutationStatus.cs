namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Defines closed privacy-safe Human Input request lifecycle outcomes.</summary>
public enum HumanInputRequestLifecycleMutationStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The command was malformed or outside schema-1 bounds.</summary>
    Invalid = 1,
    /// <summary>The lifecycle operation committed durably.</summary>
    Committed = 2,
    /// <summary>The exact durable operation was replayed.</summary>
    Replayed = 3,
    /// <summary>Optimistic state, immutable intent, or lifecycle posture conflicted.</summary>
    Conflict = 4,
    /// <summary>The exact target lifecycle was not found.</summary>
    NotFound = 5,
    /// <summary>The authenticated actor was denied.</summary>
    Denied = 6,
    /// <summary>The required exact active grant could not be established.</summary>
    GrantUnavailable = 7,
    /// <summary>A finite schema-1 bound was exhausted.</summary>
    LimitExceeded = 8,
    /// <summary>A required server-owned dependency was unavailable.</summary>
    Unavailable = 9,
    /// <summary>Available evidence could not establish one safe result.</summary>
    Ambiguous = 10,
}
