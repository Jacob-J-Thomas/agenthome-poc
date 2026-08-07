namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Defines the closed schema-1 credential contract rejection vocabulary.</summary>
public enum CredentialContractErrorCode
{
    /// <summary>No supported rejection was selected.</summary>
    Unknown = 0,
    /// <summary>A credential reference is required.</summary>
    CredentialReferenceRequired,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion,
    /// <summary>The reference identity is invalid.</summary>
    InvalidReferenceId,
    /// <summary>The reference type is invalid.</summary>
    InvalidReferenceType,
    /// <summary>The lifecycle status is invalid.</summary>
    InvalidLifecycleStatus,
    /// <summary>The owner identity is invalid.</summary>
    InvalidOwnerId,
    /// <summary>The public purpose is invalid.</summary>
    InvalidPurpose,
    /// <summary>The provider identity is invalid.</summary>
    InvalidProviderId,
    /// <summary>A timestamp is invalid or non-UTC.</summary>
    InvalidTimestamp,
    /// <summary>The reference expiry is invalid.</summary>
    InvalidExpiry,
    /// <summary>The metadata map is invalid or unbounded.</summary>
    InvalidMetadata,
    /// <summary>A metadata key is not allowlisted.</summary>
    MetadataKeyNotAllowed,
    /// <summary>A metadata value is invalid.</summary>
    InvalidMetadataValue,
    /// <summary>A credential scope is required.</summary>
    CredentialScopeRequired,
    /// <summary>A scope dimension is invalid.</summary>
    InvalidScopeDimension,
    /// <summary>A loop revision is invalid or ambiguous.</summary>
    InvalidLoopRevision,
    /// <summary>A loop scope lacks its required contextual role.</summary>
    AmbiguousLoopScope,
    /// <summary>A node lacks a containing loop.</summary>
    AmbiguousNodeScope,
    /// <summary>Capability identity and implementation are incomplete.</summary>
    AmbiguousCapabilityScope,
    /// <summary>The exact capability identity is invalid.</summary>
    InvalidCapabilityIdentity,
    /// <summary>The capability implementation identity is invalid.</summary>
    InvalidCapabilityImplementation,
    /// <summary>A target lacks a containing service.</summary>
    AmbiguousTargetScope,
    /// <summary>The scope time interval is empty.</summary>
    EmptyTimeScope,
    /// <summary>A credential capability binding is required.</summary>
    CredentialBindingRequired,
    /// <summary>The declared capability requirement is invalid.</summary>
    InvalidSecretRequirement,
    /// <summary>The binding and scope capability identities differ.</summary>
    BindingScopeMismatch,
    /// <summary>An authority proof is required.</summary>
    CredentialAuthorityProofRequired,
    /// <summary>The proof identity is invalid.</summary>
    InvalidProofId,
    /// <summary>The binding hash is invalid.</summary>
    InvalidBindingHash,
    /// <summary>The proof actor and granted scope differ.</summary>
    ProofActorMismatch,
    /// <summary>The run identity is invalid.</summary>
    InvalidRunId,
    /// <summary>The proof covers a different runtime invocation.</summary>
    ProofRunMismatch,
    /// <summary>The authority revision is invalid.</summary>
    InvalidAuthorityRevision,
    /// <summary>The proof lifetime is invalid.</summary>
    InvalidProofLifetime,
    /// <summary>The proof issuer is invalid.</summary>
    InvalidIssuerId,
    /// <summary>The proof authenticator is invalid.</summary>
    InvalidAuthenticator,
    /// <summary>Credential use evidence is required.</summary>
    CredentialUseEvidenceRequired,
    /// <summary>Use evidence identity is invalid.</summary>
    InvalidEvidenceIdentity,
    /// <summary>The use outcome is invalid.</summary>
    InvalidUseOutcome,
    /// <summary>Required redaction was not applied.</summary>
    RedactionNotApplied,
    /// <summary>A credential use request is required.</summary>
    CredentialUseRequestRequired,
    /// <summary>The supplied binding hash differs from the binding.</summary>
    BindingHashMismatch,
    /// <summary>The proof covers a different binding.</summary>
    ProofBindingMismatch,
    /// <summary>The proof covers a different reference.</summary>
    ProofReferenceMismatch,
    /// <summary>The requested scope exceeds binding or proof scope.</summary>
    CredentialScopeMismatch,
    /// <summary>The proof is not current at request time.</summary>
    CredentialProofExpired,
    /// <summary>The request time is outside its own narrowed scope.</summary>
    CredentialRequestedOutsideScope,
    /// <summary>A scope was invalid before comparison or intersection.</summary>
    InvalidCredentialScope,
    /// <summary>Credential scope dimensions conflict.</summary>
    CredentialScopeConflict,
    /// <summary>Credential scope time intervals do not overlap.</summary>
    CredentialScopeTimeConflict,
    /// <summary>Credential scope intersection was ambiguous.</summary>
    AmbiguousCredentialScope,
    /// <summary>A credential contract hash is invalid.</summary>
    InvalidCredentialContractHash,
    /// <summary>A credential reference identifier is invalid.</summary>
    InvalidCredentialReferenceId,
    /// <summary>A credential provider identifier is invalid.</summary>
    InvalidCredentialProviderId,
    /// <summary>A credential contract identifier is invalid.</summary>
    InvalidCredentialContractId,
    /// <summary>Canonical credential JSON exceeds the schema bound.</summary>
    CredentialContractTooLarge,
    /// <summary>Credential JSON is malformed or outside the closed schema.</summary>
    InvalidCredentialJson,
    /// <summary>Credential JSON is not in exact canonical form.</summary>
    NoncanonicalCredentialJson,
    /// <summary>A decoded credential reference is invalid.</summary>
    InvalidCredentialReference,
    /// <summary>A decoded credential binding is invalid.</summary>
    InvalidCredentialBinding,
    /// <summary>A decoded credential authority proof is invalid.</summary>
    InvalidCredentialAuthorityProof,
    /// <summary>Decoded use evidence is invalid.</summary>
    InvalidCredentialUseEvidence,
    /// <summary>A provider credential length is invalid.</summary>
    InvalidCredentialLength,
    /// <summary>A provider result posture is invalid.</summary>
    InvalidProviderResult,
    /// <summary>A provider request identity is invalid.</summary>
    InvalidProviderRequestIdentity,
    /// <summary>The validation error collection exceeded its bound.</summary>
    ValidationLimitExceeded
}
