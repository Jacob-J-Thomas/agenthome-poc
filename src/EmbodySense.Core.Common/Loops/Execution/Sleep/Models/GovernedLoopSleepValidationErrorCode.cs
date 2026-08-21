namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Identifies one closed, value-free sleep/wake/coordinator contract rejection.</summary>
public enum GovernedLoopSleepValidationErrorCode
{
    /// <summary>A required contract or field is absent.</summary>
    Required = 1,

    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>An identifier is not a bounded canonical lowercase token.</summary>
    InvalidIdentity = 3,

    /// <summary>A closed enumeration contains an unsupported value.</summary>
    InvalidEnumeration = 4,

    /// <summary>A hash is not canonical lowercase SHA-256 evidence.</summary>
    InvalidHash = 5,

    /// <summary>A retained content hash or deterministic identity does not match immutable content.</summary>
    IntegrityMismatch = 6,

    /// <summary>A timestamp or trusted-time relationship is invalid.</summary>
    InvalidTimestamp = 7,

    /// <summary>A numeric schema bound was exceeded.</summary>
    LimitExceeded = 8,

    /// <summary>An exact execution, publication, frontier, cycle, visit, or attempt binding is inconsistent.</summary>
    BindingMismatch = 9,

    /// <summary>A wake condition, authenticated-event proof, or disposition shape is inconsistent.</summary>
    InvalidComposition = 10,

    /// <summary>A proposed optimistic successor version is not contiguous.</summary>
    InvalidSuccessorVersion = 11,

    /// <summary>An immutable identity changed across a transition.</summary>
    ImmutableEvidenceChanged = 12,

    /// <summary>A proposed wake or coordinator state transition is illegal.</summary>
    IllegalTransition = 13
}
