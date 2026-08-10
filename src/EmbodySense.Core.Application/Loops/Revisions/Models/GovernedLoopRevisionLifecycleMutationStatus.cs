namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies the closed application outcome of one governed-loop revision lifecycle request.</summary>
public enum GovernedLoopRevisionLifecycleMutationStatus
{
    /// <summary>An undefined result that the service never returns.</summary>
    Unknown = 0,
    /// <summary>The requested terminal outcome was newly committed.</summary>
    Committed = 1,
    /// <summary>The exact request replayed its previously committed terminal outcome.</summary>
    Replayed = 2,
    /// <summary>The request failed deterministic contract validation before authority or persistence.</summary>
    Invalid = 3,
    /// <summary>The authenticated actor was not authorized for the exact request.</summary>
    Unauthorized = 4,
    /// <summary>The expected lifecycle or workspace-global operation binding conflicted.</summary>
    Conflict = 5,
    /// <summary>The requested graph, revision, or historical publication did not exist.</summary>
    NotFound = 6,
    /// <summary>A finite contract or retained-history ceiling was reached.</summary>
    LimitExceeded = 7,
    /// <summary>The candidate publication failed deterministic server-side validation.</summary>
    PublicationRejected = 8,
    /// <summary>No durable intent was published because a required authority, validation, or persistence dependency was unavailable.</summary>
    Unavailable = 9,
    /// <summary>Durable evidence cannot prove whether the exact operation committed.</summary>
    Ambiguous = 10,
}
