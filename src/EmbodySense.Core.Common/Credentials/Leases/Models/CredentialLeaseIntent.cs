namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Defines one exact, value-free, short-lived, nonrenewable credential-redemption intent.</summary>
public sealed record CredentialLeaseIntent(
    int SchemaVersion,
    string LeaseId,
    string CredentialUseOperationId,
    long CredentialUseGeneration,
    CredentialLeaseExecutionScope Execution,
    CredentialLeaseAuthorityScope Authority,
    CredentialLeaseEffectScope Effect,
    CredentialLeaseCapabilityScope Capability,
    CredentialLeaseProfileScope Profile,
    CredentialLeaseRegistryScope Registry,
    CredentialLeaseTargetScope Target,
    DateTimeOffset IssuedAtUtc,
    CredentialLeaseDeadlines Deadlines,
    DateTimeOffset EffectiveExpiresAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
