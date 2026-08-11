namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one deterministic authenticated-response contract violation.</summary>
public enum HumanInputResponseValidationErrorCode
{
    /// <summary>No supported validation code was supplied.</summary>
    Unknown = 0,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 1,
    /// <summary>A stable identifier is not canonical.</summary>
    InvalidIdentifier = 2,
    /// <summary>A required canonical digest is malformed or mismatched.</summary>
    InvalidHash = 3,
    /// <summary>The exact request reference is malformed or mismatched.</summary>
    InvalidRequestReference = 4,
    /// <summary>The exact request binding is malformed or mismatched.</summary>
    InvalidBinding = 5,
    /// <summary>Authenticated actor attribution is absent or malformed.</summary>
    InvalidActor = 6,
    /// <summary>Trusted eligible role attribution is absent or malformed.</summary>
    InvalidRole = 7,
    /// <summary>A required trusted time is default or not UTC.</summary>
    InvalidUtcTime = 8,
    /// <summary>The retained privacy class is unsupported or mismatched.</summary>
    InvalidPrivacyClass = 9,
    /// <summary>The typed response value or explanation is invalid for the exact request.</summary>
    InvalidValue = 10,
    /// <summary>An exact response reference is malformed or mismatched.</summary>
    InvalidResponseReference = 11,
    /// <summary>An exact response selection reference is malformed or mismatched.</summary>
    InvalidSelectionReference = 12,
    /// <summary>A selection has an impossible, unbounded, duplicate, or cross-bound shape.</summary>
    InvalidSelectionShape = 13,
    /// <summary>Operation evidence has an impossible field shape.</summary>
    InvalidEvidenceShape = 14,
    /// <summary>The response operation kind is absent, unknown, or unsupported.</summary>
    InvalidOperationKind = 15,
    /// <summary>The response operation outcome is absent, unknown, or unsupported.</summary>
    InvalidOperationOutcome = 16,
    /// <summary>The operation failure code is absent, unsupported, or inconsistent with its outcome and kind.</summary>
    InvalidFailureCode = 17,
    /// <summary>The optimistic or resulting request lifecycle state is invalid.</summary>
    InvalidLifecycleState = 18,
    /// <summary>Server-owned authentication evidence is absent or malformed.</summary>
    InvalidAuthenticationEvidence = 19,
    /// <summary>Exact request-policy eligibility evidence is absent or malformed.</summary>
    InvalidEligibilityEvidence = 20
}
