namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Identifies one deterministic Human Input request lifecycle contract violation.</summary>
public enum HumanInputRequestLifecycleValidationErrorCode
{
    /// <summary>No supported validation code was supplied.</summary>
    Unknown = 0,
    /// <summary>The artifact schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 1,
    /// <summary>A stable identifier is not canonical.</summary>
    InvalidIdentifier = 2,
    /// <summary>A required digest is not canonical.</summary>
    InvalidHash = 3,
    /// <summary>A required time is default or not UTC.</summary>
    InvalidUtcTime = 4,
    /// <summary>An exact immutable request reference is malformed or mismatched.</summary>
    InvalidRequestReference = 5,
    /// <summary>An optimistic lifecycle version is outside schema-1 bounds.</summary>
    InvalidLifecycleVersion = 6,
    /// <summary>A lifecycle posture is absent, unknown, or unsupported.</summary>
    InvalidLifecycleStatus = 7,
    /// <summary>A reminder count is outside schema-1 bounds.</summary>
    InvalidReminderCount = 8,
    /// <summary>Supersession lineage is incomplete, self-referential, or inconsistent.</summary>
    InvalidSupersession = 9,
    /// <summary>An operation kind is absent, unknown, or unsupported.</summary>
    InvalidOperationKind = 10,
    /// <summary>An operation outcome is absent, unknown, or unsupported.</summary>
    InvalidOperationOutcome = 11,
    /// <summary>An operation failure code is absent, unknown, or unsupported.</summary>
    InvalidFailureCode = 12,
    /// <summary>The terminal outcome and failure code are inconsistent.</summary>
    InvalidOutcomeFailurePair = 13,
    /// <summary>A lifecycle head has an impossible field shape.</summary>
    InvalidHeadShape = 14,
    /// <summary>Operation evidence has an impossible field shape.</summary>
    InvalidEvidenceShape = 15,
    /// <summary>Authenticated actor or bounded non-secret reason attribution is absent.</summary>
    InvalidAttribution = 16,
    /// <summary>Server-owned actor-authority evidence is absent or malformed.</summary>
    InvalidAuthorityEvidence = 17,
    /// <summary>Exact grant or dependency evidence is absent, malformed, or unexpected.</summary>
    InvalidGrantEvidence = 18,
    /// <summary>The proposed committed lifecycle transition is not legal.</summary>
    InvalidTransition = 19,
    /// <summary>A request candidate changed a field outside the requested operation.</summary>
    RequestMutationOutsideOperation = 20,
    /// <summary>A replacement request weakens the retained privacy classification.</summary>
    PrivacyDowngrade = 21,
    /// <summary>The trusted time does not satisfy the requested transition boundary.</summary>
    TimingBoundaryConflict = 22
}
