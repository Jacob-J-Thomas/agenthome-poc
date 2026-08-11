namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one stable governed-loop admission contract validation failure.</summary>
public enum GovernedLoopAdmissionValidationErrorCode
{
    /// <summary>A required contract or field is absent.</summary>
    Required = 1,

    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>An identifier, workspace, actor, or surface is not canonical.</summary>
    InvalidIdentity = 3,

    /// <summary>A closed enumeration contains an unsupported value.</summary>
    InvalidEnumeration = 4,

    /// <summary>A timestamp is absent, non-UTC, or chronologically inconsistent.</summary>
    InvalidTimestamp = 5,

    /// <summary>A hash does not use the exact canonical representation.</summary>
    InvalidHash = 6,

    /// <summary>A stored hash does not match recomputed immutable content.</summary>
    HashMismatch = 7,

    /// <summary>A nested exact pin, binding, ceiling, or capability snapshot is invalid.</summary>
    InvalidEvidence = 8,

    /// <summary>A finite collection or numeric bound was exceeded.</summary>
    LimitExceeded = 9,

    /// <summary>An evidence kind was duplicated or omitted.</summary>
    EvidenceSetMismatch = 10,

    /// <summary>Exact workspace, revision, role, grant, or evidence bindings disagree.</summary>
    BindingMismatch = 11,

    /// <summary>The terminal receipt, successful evidence, and rejection composition is inconsistent.</summary>
    InvalidComposition = 12
}
