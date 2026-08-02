using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns an immutable admission snapshot or a bounded fail-closed explanation.</summary>
public sealed record CapabilityAdmissionResult(bool IsAdmitted, CapabilityAdmissionSnapshot? Snapshot, string Detail);
