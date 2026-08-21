namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryEvidenceReservationDocument(
    string EvidenceId,
    string CredentialUseOperationId,
    long CredentialUseGeneration,
    string IntentHash,
    string ReferenceId,
    string BindingHash);
