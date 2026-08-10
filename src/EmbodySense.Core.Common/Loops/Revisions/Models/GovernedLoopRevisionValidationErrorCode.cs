namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies one closed, value-free revision-contract rejection.</summary>
public enum GovernedLoopRevisionValidationErrorCode
{
    /// <summary>No supported rejection was supplied.</summary>
    Unknown = 0,
    /// <summary>A required contract or field is absent.</summary>
    ContractRequired,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion,
    /// <summary>An identifier is non-canonical or exceeds its finite bound.</summary>
    InvalidIdentifier,
    /// <summary>A digest is not canonical lowercase SHA-256 hexadecimal.</summary>
    InvalidHash,
    /// <summary>A timestamp is default or does not use the zero UTC offset.</summary>
    InvalidTimestamp,
    /// <summary>An enum value is unknown or outside its closed vocabulary.</summary>
    InvalidEnumeration,
    /// <summary>A lifecycle version is outside its finite supported range.</summary>
    InvalidLifecycleVersion,
    /// <summary>Related revision references do not identify the same graph.</summary>
    GraphMismatch,
    /// <summary>Lineage contains an illegal self-reference or rollback shape.</summary>
    InvalidLineage,
    /// <summary>A lifecycle head does not compose with its posture and exact heads.</summary>
    InvalidHeadComposition,
    /// <summary>A proposed lifecycle transition is illegal.</summary>
    IllegalTransition,
    /// <summary>A successor optimistic version is not contiguous.</summary>
    InvalidSuccessorVersion,
    /// <summary>An immutable publication pin changed unexpectedly.</summary>
    PublicationPinChanged
}
