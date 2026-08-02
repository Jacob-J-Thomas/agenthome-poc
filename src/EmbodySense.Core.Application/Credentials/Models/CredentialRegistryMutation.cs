using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Application.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Requests one idempotent optimistic credential-registry transition.</summary>
public sealed record CredentialRegistryMutation(CredentialRegistryMutationKind Kind, CredentialContractId OperationId, long ExpectedRegistryRevision, CredentialReferenceId ReferenceId, CredentialReference? Reference, CredentialCapabilityBinding? Binding, CredentialContractId? ConsentReference, CredentialProviderHealthStatus? Health, CredentialProviderLocator? ProviderLocator, bool? ConsentGranted = null, int? LifecycleOperation = null, string? ActorId = null, string? PreviewHash = null, string? LifecycleRequestHash = null, CredentialLifecycleMutationPhase? LifecyclePhase = null, CredentialContractId? LifecycleIntentOperationId = null, IReadOnlyList<string>? AffectedActiveRuns = null, string? WorkspaceId = null, CredentialLifecycleAuditPayload? LifecycleAudit = null);
