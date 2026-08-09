namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryEntryDocument(string ReferenceJson, string BindingJson, string BindingHash, string ConsentReference, int Health, long Revision, string LastOperationId);
