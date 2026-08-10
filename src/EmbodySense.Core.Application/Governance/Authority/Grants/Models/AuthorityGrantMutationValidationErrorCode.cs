namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies one bounded value-free mutation-request error.</summary>
public enum AuthorityGrantMutationValidationErrorCode
{
    /// <summary>A request was not supplied.</summary>
    RequestRequired = 1,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>The operation identity is malformed.</summary>
    InvalidOperationId = 3,
    /// <summary>The grant identity is malformed.</summary>
    InvalidGrantId = 4,
    /// <summary>The optimistic revision is invalid.</summary>
    InvalidExpectedRevision = 5,
    /// <summary>The expected lifecycle posture is invalid.</summary>
    InvalidExpectedStatus = 6,
    /// <summary>The operation kind is unsupported.</summary>
    InvalidOperationKind = 7,
    /// <summary>The candidate successor fields are malformed or inconsistent.</summary>
    InvalidCandidate = 8,
    /// <summary>The actor attribution is missing.</summary>
    InvalidActor = 9,
    /// <summary>The lifecycle reason is missing.</summary>
    InvalidReason = 10,
    /// <summary>The supplied request hash does not match canonical intent.</summary>
    RequestHashMismatch = 11,
}
