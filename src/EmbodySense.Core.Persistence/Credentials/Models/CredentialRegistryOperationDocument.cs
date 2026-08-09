namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryOperationDocument(string OperationId, string RequestHash, int Kind, long Revision, string ReferenceId, CredentialRegistryEntryDocument? ResultEntry);
