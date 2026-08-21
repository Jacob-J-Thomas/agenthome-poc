using EmbodySense.Core.Common.Credentials.Leases;

namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Records bounded, value-free evidence of one trusted credential use.</summary>
public sealed record CredentialUseEvidence(
    int SchemaVersion,
    CredentialContractId EvidenceId,
    CredentialReferenceId ReferenceId,
    CredentialContractHash BindingHash,
    CredentialContractId ProofId,
    CredentialContractId RunId,
    CredentialScope UsedScope,
    DateTimeOffset UsedAtUtc,
    CredentialUseOutcome Outcome,
    bool RedactionApplied,
    CredentialLeaseUseEvidence? Lease = null)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
