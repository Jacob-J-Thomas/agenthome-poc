using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Represents one immutable, non-transferable, hash-bound delegated-authority envelope.</summary>
public sealed record AuthorityDelegationEnvelope
{
    /// <summary>Creates an envelope while defensively copying every nested authority value.</summary>
    public AuthorityDelegationEnvelope(
        int schemaVersion,
        string envelopeId,
        AuthorityDelegationParentEvidenceReference parentEvidence,
        AuthorityDelegationTargetBinding target,
        AuthorityCeiling delegatedCeiling,
        IReadOnlyList<CapabilityAdmissionPin> delegatedCapabilityPins,
        string targetClass,
        string operationClass,
        AuthorityPurpose purpose,
        AuthorityDelegationBoundary boundary,
        AuthorityDelegationRevocationLink revocationLink,
        AuthorityDelegationSubsetProof subsetProof,
        DateTimeOffset issuedAtUtc,
        string contentHash)
    {
        SchemaVersion = schemaVersion;
        EnvelopeId = envelopeId;
        ParentEvidence = AuthorityDelegationContractCopy.Copy(parentEvidence);
        Target = AuthorityDelegationContractCopy.Copy(target);
        DelegatedCeiling = AuthorityDelegationContractCopy.Copy(delegatedCeiling);
        DelegatedCapabilityPins = AuthorityDelegationContractCopy.CopyPins(delegatedCapabilityPins);
        TargetClass = targetClass;
        OperationClass = operationClass;
        Purpose = purpose;
        Boundary = AuthorityDelegationContractCopy.Copy(boundary);
        RevocationLink = AuthorityDelegationContractCopy.Copy(revocationLink);
        SubsetProof = AuthorityDelegationContractCopy.Copy(subsetProof);
        IssuedAtUtc = issuedAtUtc;
        ContentHash = contentHash;
    }

    /// <summary>Gets the only supported experimental envelope schema version.</summary>
    public const int CurrentSchemaVersion = AuthorityDelegationContractLimits.CurrentSchemaVersion;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable idempotent envelope identity.</summary>
    public string EnvelopeId { get; }

    /// <summary>Gets the exact parent authority and issuer evidence.</summary>
    public AuthorityDelegationParentEvidenceReference ParentEvidence { get; }

    /// <summary>Gets the exact immutable delegation target.</summary>
    public AuthorityDelegationTargetBinding Target { get; }

    /// <summary>Gets the delegated authority ceiling, which grants nothing by itself.</summary>
    public AuthorityCeiling DelegatedCeiling { get; }

    /// <summary>Gets the exact delegated capability pins.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> DelegatedCapabilityPins { get; }

    /// <summary>Gets the exact non-wildcard target class.</summary>
    public string TargetClass { get; }

    /// <summary>Gets the exact non-wildcard operation class.</summary>
    public string OperationClass { get; }

    /// <summary>Gets the exact bounded purpose restriction.</summary>
    public AuthorityPurpose Purpose { get; }

    /// <summary>Gets the local trusted-time and completion boundary.</summary>
    public AuthorityDelegationBoundary Boundary { get; }

    /// <summary>Gets the exact parent revocation and completion link.</summary>
    public AuthorityDelegationRevocationLink RevocationLink { get; }

    /// <summary>Gets the hash-only, server-recomputable subset proof.</summary>
    public AuthorityDelegationSubsetProof SubsetProof { get; }

    /// <summary>Gets the trusted UTC issue time.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; init; }
}
