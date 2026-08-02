using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Requests one exact, workspace- and actor-bound destructive lifecycle preview.</summary>
public sealed record CredentialLifecyclePreviewRequest(CredentialContractId OperationId, CredentialLifecycleOperationKind Kind, CredentialReferenceId ReferenceId, string WorkspaceId, string ActorId, long ExpectedRegistryRevision, CredentialContractId? InterruptedRepairOperationId = null);
