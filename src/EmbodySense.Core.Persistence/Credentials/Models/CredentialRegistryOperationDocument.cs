namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryOperationDocument(string OperationId, string RequestHash, int Kind, long Revision, string ReferenceId, CredentialRegistryEntryDocument? ResultEntry, int? LifecycleOperation = null, string? ActorId = null, string? PreviewHash = null, string? LifecycleRequestHash = null, int? LifecyclePhase = null, string? LifecycleIntentOperationId = null, IReadOnlyList<string>? AffectedActiveRuns = null, string? WorkspaceId = null, CredentialRegistryAuditOutboxDocument? AuditOutbox = null);
