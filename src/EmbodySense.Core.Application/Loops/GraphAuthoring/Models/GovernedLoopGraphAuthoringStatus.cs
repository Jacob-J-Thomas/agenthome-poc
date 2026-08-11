namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Identifies the closed outcome of one graph authoring operation.</summary>
public enum GovernedLoopGraphAuthoringStatus
{
    /// <summary>An undefined outcome that a conforming service never returns.</summary>
    Unknown = 0,
    /// <summary>The exact operation committed.</summary>
    Committed = 1,
    /// <summary>The exact durable full-intent operation replayed.</summary>
    Replayed = 2,
    /// <summary>The request or graph shape was invalid.</summary>
    Invalid = 3,
    /// <summary>The graph was structurally valid but failed current catalog or authority admission.</summary>
    ValidationRejected = 4,
    /// <summary>The actor lacked authority for the lifecycle operation.</summary>
    Unauthorized = 5,
    /// <summary>Optimistic state or a workspace-global operation binding conflicted.</summary>
    Conflict = 6,
    /// <summary>A required graph, revision, or historical publication was absent.</summary>
    NotFound = 7,
    /// <summary>A finite artifact or evidence limit was reached.</summary>
    LimitExceeded = 8,
    /// <summary>The exact publication candidate failed current graph validation.</summary>
    PublicationRejected = 9,
    /// <summary>No durable intent was published because a dependency was unavailable.</summary>
    Unavailable = 10,
    /// <summary>Durable evidence cannot prove whether the operation committed.</summary>
    Ambiguous = 11,
}
