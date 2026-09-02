namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies one closed, value-free effect-reconciliation contract rejection.</summary>
public enum GovernedLoopEffectReconciliationValidationErrorCode
{
    /// <summary>A required contract or field is absent.</summary>
    Required = 1,

    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>An identifier is not canonical and bounded.</summary>
    InvalidIdentity = 3,

    /// <summary>A closed enumeration contains an unsupported value.</summary>
    InvalidEnumeration = 4,

    /// <summary>A hash is not canonical lowercase SHA-256 evidence.</summary>
    InvalidHash = 5,

    /// <summary>A retained content hash does not match immutable content.</summary>
    IntegrityMismatch = 6,

    /// <summary>A timestamp or trusted-time relationship is invalid.</summary>
    InvalidTimestamp = 7,

    /// <summary>A finite schema bound was exceeded.</summary>
    LimitExceeded = 8,

    /// <summary>Exact execution or effect coordinates are inconsistent.</summary>
    BindingMismatch = 9,

    /// <summary>Evidence fields do not compose into a legal observation or assessment.</summary>
    InvalidComposition = 10,

    /// <summary>A retained collection is not in canonical ordinal order or contains duplicates.</summary>
    NonCanonicalOrder = 11,

    /// <summary>The disposition does not match the exact current assessment.</summary>
    IllegalDisposition = 12,

    /// <summary>The resolution is absent, unexpected, or does not match the accepted disposition.</summary>
    IllegalResolution = 13
}
