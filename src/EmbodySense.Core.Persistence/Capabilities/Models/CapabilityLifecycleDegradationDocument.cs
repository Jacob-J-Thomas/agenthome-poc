namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityLifecycleDegradationDocument(string CapabilityId, string OperationId, string DependentKind, string DependentIdentity, string DependentRevision, string CompatibleVersionRange, DateTimeOffset RecordedAtUtc);
