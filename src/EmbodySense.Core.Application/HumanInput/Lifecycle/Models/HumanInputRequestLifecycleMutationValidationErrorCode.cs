namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Identifies bounded Human Input lifecycle command validation failures.</summary>
public enum HumanInputRequestLifecycleMutationValidationErrorCode
{
    /// <summary>No supported validation code was supplied.</summary>
    Unknown = 0,
    /// <summary>The command is required.</summary>
    CommandRequired = 1,
    /// <summary>The command schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>An identifier is missing, malformed, or outside its bound.</summary>
    InvalidIdentifier = 3,
    /// <summary>The lifecycle operation kind is unsupported.</summary>
    InvalidOperationKind = 4,
    /// <summary>The expected lifecycle state is malformed for this operation.</summary>
    InvalidExpectedState = 5,
    /// <summary>The candidate request is missing, malformed, or present for the wrong operation.</summary>
    InvalidCandidateRequest = 6,
    /// <summary>The authority-grant reference is missing, malformed, or present for a cleanup operation.</summary>
    InvalidGrantReference = 7,
    /// <summary>The bounded non-secret reason is missing or malformed.</summary>
    InvalidReason = 8,
    /// <summary>The canonical command hash is missing or does not match.</summary>
    InvalidRequestHash = 9,
    /// <summary>The command shape does not match the requested operation.</summary>
    InvalidOperationShape = 10,
}
