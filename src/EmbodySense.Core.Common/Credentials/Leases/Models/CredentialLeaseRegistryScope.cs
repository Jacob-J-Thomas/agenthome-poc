namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a credential lease to one coherent registry snapshot without retaining a private provider locator.</summary>
public sealed record CredentialLeaseRegistryScope(
    string ReferenceId,
    string BindingHash,
    long RegistryRevision,
    string ConsentReferenceId,
    string ProviderId);
