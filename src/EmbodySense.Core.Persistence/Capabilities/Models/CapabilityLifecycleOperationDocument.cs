using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityLifecycleOperationDocument(string OperationId, string RequestHash, string? SelectionRequestHash, CapabilityLifecycleOperationKind Kind, string CapabilityId, string? TargetDescriptorJson, string? TargetArtifactDigest, long BaselineCatalogRevision, long BaselineActivationRevision, long PreviewRevision, long DependentSetRevision, string DependentSetHash, string PreviewHash, IReadOnlyList<CapabilityLifecycleImpact> Impacts, CapabilityLifecycleMutationStatus? Outcome, long? ResultRevision, bool OutcomeAuditPending);
