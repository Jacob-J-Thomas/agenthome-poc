using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Requests one idempotent actor-bound lifecycle transition without carrying secret material or a provider locator.</summary>
public sealed record CredentialLifecycleRequest(CredentialLifecycleOperationKind Kind, CredentialContractId OperationId, CredentialReferenceId ReferenceId, string WorkspaceId, string ActorId, long ExpectedRegistryRevision, DateTimeOffset RequestedAtUtc, int ValueByteLength = 0, CredentialReference? Reference = null, CredentialCapabilityBinding? Binding = null, CredentialContractId? ConsentReference = null, CredentialLifecyclePreview? Preview = null, bool Confirmed = false, CredentialContractId? InterruptedRepairOperationId = null);
