using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains one correlation-bound credential lifecycle event awaiting at-least-once audit delivery.</summary>
public sealed record CredentialLifecycleAuditOutboxItem(CredentialContractId AuditOperationId, CredentialContractId LifecycleIntentOperationId, CredentialReferenceId ReferenceId, string WorkspaceId, string ActorId, CredentialLifecycleOperationKind Kind, DateTimeOffset OccurredAtUtc, long RegistryRevision, string? PreviewRevision, string Action, string Outcome, string Detail);
