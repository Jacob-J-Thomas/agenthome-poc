namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityLifecycleEntryDocument(string CapabilityId, string DescriptorJson, string ArtifactDigest, bool IsEnabled, bool IsRemoved, long Revision, string LastOperationId, DateTimeOffset UpdatedAtUtc, long BaselineCatalogRevision, long BaselineActivationRevision, IReadOnlyList<CapabilityLifecycleHistoryDocument> History, IReadOnlyList<CapabilityLifecycleDegradationDocument> Degradations);
