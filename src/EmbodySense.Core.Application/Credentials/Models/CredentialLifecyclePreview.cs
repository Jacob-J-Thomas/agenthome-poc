using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Contains one bounded value-free preview bound to an exact workspace, registry revision, and dependent revision.</summary>
public sealed record CredentialLifecyclePreview(CredentialLifecyclePreviewStatus Status, CredentialContractId OperationId, CredentialLifecycleOperationKind Kind, CredentialReferenceId ReferenceId, string WorkspaceId, string ActorId, long? RegistryRevision, string DependentSetRevision, string PreviewRevision, IReadOnlyList<CredentialLifecycleImpact> Impacts, string Detail)
{
    /// <summary>Gets a defensive immutable impact snapshot.</summary>
    public IReadOnlyList<CredentialLifecycleImpact> Impacts { get; } = Array.AsReadOnly((Impacts ?? []).ToArray());
}
