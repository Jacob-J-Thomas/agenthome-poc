namespace EmbodySense.Core.Common.Loops.Execution.Wait.Models;

/// <summary>Identifies one closed, value-free Wait contract rejection.</summary>
public enum GovernedLoopWaitValidationErrorCode
{
    /// <summary>A required contract or field is absent.</summary>
    Required = 1,

    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>The node descriptor is not in the exact closed Wait catalog.</summary>
    InvalidDescriptor = 3,

    /// <summary>The descriptor parameter set or typed parameter kind is not exact.</summary>
    InvalidParameter = 4,

    /// <summary>An identifier is not bounded and canonical.</summary>
    InvalidIdentity = 5,

    /// <summary>A retained timestamp is not an exact trusted UTC value.</summary>
    InvalidTimestamp = 6,

    /// <summary>A hash is not canonical lowercase SHA-256 evidence.</summary>
    InvalidHash = 7,

    /// <summary>Retained evidence does not match its canonical content.</summary>
    IntegrityMismatch = 8,

    /// <summary>Wait, sleep, wake, and continuation fields do not compose.</summary>
    InvalidComposition = 9,

    /// <summary>Exact checkpoint, wake, or frontier coordinates do not match.</summary>
    BindingMismatch = 10,

    /// <summary>A finite schema-1 numeric bound was exceeded.</summary>
    LimitExceeded = 11,

    /// <summary>The resumed frontier version is not the exact contiguous successor.</summary>
    InvalidSuccessorVersion = 12
}
