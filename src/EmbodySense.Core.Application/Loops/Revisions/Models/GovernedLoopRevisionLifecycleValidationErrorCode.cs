namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Identifies one bounded, value-free lifecycle request rejection.</summary>
public enum GovernedLoopRevisionLifecycleValidationErrorCode
{
    /// <summary>No supported error was supplied.</summary>
    Unknown = 0,
    /// <summary>The request is absent.</summary>
    RequestRequired = 1,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>An identifier is not canonical.</summary>
    InvalidIdentifier = 3,
    /// <summary>The operation kind is unknown or unsupported.</summary>
    InvalidOperationKind = 4,
    /// <summary>The actor identity is absent.</summary>
    InvalidActor = 5,
    /// <summary>The optimistic lifecycle expectation is malformed.</summary>
    InvalidLifecycleExpectation = 6,
    /// <summary>A required revision or publication pin is absent.</summary>
    RequiredReferenceMissing = 7,
    /// <summary>An unexpected revision or publication pin was supplied.</summary>
    UnexpectedReference = 8,
    /// <summary>A supplied revision or pin is malformed.</summary>
    InvalidReference = 9,
    /// <summary>A supplied revision or pin belongs to a different graph.</summary>
    GraphMismatch = 10,
    /// <summary>The candidate duplicates an existing or rollback-source revision identity.</summary>
    CandidateNotDistinct = 11,
    /// <summary>The rollback candidate executable hash does not match its exact historical source.</summary>
    RollbackContentMismatch = 12,
}
