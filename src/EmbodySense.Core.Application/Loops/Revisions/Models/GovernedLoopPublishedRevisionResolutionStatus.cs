namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies whether one exact publication pin is currently executable without selecting a replacement.</summary>
public enum GovernedLoopPublishedRevisionResolutionStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact pin remains the graph's active publication.</summary>
    Active = 1,
    /// <summary>The exact pin is current but explicitly disabled.</summary>
    Disabled = 2,
    /// <summary>The exact pin belongs to an archived terminal graph.</summary>
    Archived = 3,
    /// <summary>The pin was historically valid but is no longer the current publication.</summary>
    Stale = 4,
    /// <summary>The exact graph, revision, or publication evidence was not found.</summary>
    NotFound = 5,
    /// <summary>The supplied pin failed bounded contract validation.</summary>
    Invalid = 6,
    /// <summary>The store could not provide a trustworthy current observation.</summary>
    Unavailable = 7,
    /// <summary>Durable evidence could not prove one consistent resolution.</summary>
    Ambiguous = 8,
}
