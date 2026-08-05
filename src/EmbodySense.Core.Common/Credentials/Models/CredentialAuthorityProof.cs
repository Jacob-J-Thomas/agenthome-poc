namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Contains value-free evidence from an authority issuer; it grants nothing until a trusted verifier accepts it.</summary>
public sealed record CredentialAuthorityProof(
    int SchemaVersion,
    CredentialContractId ProofId,
    CredentialReferenceId ReferenceId,
    CredentialContractHash BindingHash,
    CredentialScope GrantedScope,
    string ActorId,
    CredentialContractId RunId,
    long AuthorityRevision,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    CredentialProviderId IssuerId,
    CredentialContractHash Authenticator)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
