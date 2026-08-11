namespace EmbodySense.Core.Common.Loops.Sequential.Models;

/// <summary>Identifies one closed value-free sequential hand-off contract rejection.</summary>
public enum GovernedLoopSequentialValidationErrorCode
{
    /// <summary>No supported rejection was supplied.</summary>
    Unknown = 0,
    /// <summary>A required contract or nested value is absent.</summary>
    Required,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion,
    /// <summary>Text is empty, unsafe, non-normalized, or outside its finite bound.</summary>
    InvalidText,
    /// <summary>An identity is not canonical.</summary>
    InvalidIdentity,
    /// <summary>A timestamp is not a non-default UTC value or violates snapshot ordering.</summary>
    InvalidTimestamp,
    /// <summary>An enumeration value is undefined.</summary>
    InvalidEnumeration,
    /// <summary>A collection exceeds its finite schema bound.</summary>
    CollectionTooLarge,
    /// <summary>A collection or nested manifest is not canonically ordered and internally consistent.</summary>
    InvalidComposition,
    /// <summary>A supplied digest is not canonical lowercase SHA-256 hexadecimal.</summary>
    InvalidHash,
    /// <summary>A canonical digest does not match the exact retained content.</summary>
    HashMismatch,
}
