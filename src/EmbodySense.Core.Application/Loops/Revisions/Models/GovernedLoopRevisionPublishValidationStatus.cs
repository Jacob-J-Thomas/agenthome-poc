namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies the server-owned validation posture of one exact publication candidate.</summary>
public enum GovernedLoopRevisionPublishValidationStatus
{
    /// <summary>No trustworthy validation decision was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact immutable revision is valid for publication.</summary>
    Valid = 1,
    /// <summary>The exact immutable revision deterministically failed publication validation.</summary>
    Invalid = 2,
    /// <summary>Current validation evidence could not be produced.</summary>
    Unavailable = 3,
}
