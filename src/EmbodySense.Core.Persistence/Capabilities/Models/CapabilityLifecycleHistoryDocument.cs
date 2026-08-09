namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityLifecycleHistoryDocument(string DescriptorJson, string ArtifactDigest, bool IsEnabled, bool IsRemoved, long Revision, string OperationId, DateTimeOffset ChangedAtUtc);
