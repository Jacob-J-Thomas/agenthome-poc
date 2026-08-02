using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains immutable, value-free evidence of one registry or credential-use operation identity.</summary>
public sealed record CredentialRegistryOperationEvidence(CredentialContractId OperationId, CredentialContractHash RequestHash, int Kind, long Revision, CredentialReferenceId ReferenceId, int? LifecycleOperation = null, string? ActorId = null, string? PreviewHash = null, string? LifecycleRequestHash = null, CredentialLifecycleMutationPhase? LifecyclePhase = null, CredentialContractId? LifecycleIntentOperationId = null, CredentialProviderHealthStatus? ResultHealth = null, IReadOnlyList<string>? AffectedActiveRuns = null, string? WorkspaceId = null)
{
    /// <summary>Gets the exact bounded active-run impact captured before a restrictive transition.</summary>
    public IReadOnlyList<string> AffectedActiveRuns { get; } = Array.AsReadOnly((AffectedActiveRuns ?? []).ToArray());
}
