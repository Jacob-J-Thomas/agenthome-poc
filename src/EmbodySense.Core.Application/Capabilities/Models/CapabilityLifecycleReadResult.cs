namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns current lifecycle state with immutable history and visible optional degradation evidence.</summary>
/// <param name="Status">The read outcome.</param>
/// <param name="State">The current or tombstoned state.</param>
/// <param name="History">The append-only prior states.</param>
/// <param name="Degradations">The current optional degradation evidence.</param>
/// <param name="LifecycleRevision">The authenticated aggregate revision when known.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
public sealed record CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus Status, CapabilityLifecycleState? State, IReadOnlyList<CapabilityLifecycleHistoryEntry> History, IReadOnlyList<CapabilityLifecycleDegradation> Degradations, long? LifecycleRevision, string Detail)
{
    /// <summary>Gets a defensive read-only history snapshot.</summary>
    public IReadOnlyList<CapabilityLifecycleHistoryEntry> History { get; } = Array.AsReadOnly((History ?? []).ToArray());
    /// <summary>Gets a defensive read-only degradation snapshot.</summary>
    public IReadOnlyList<CapabilityLifecycleDegradation> Degradations { get; } = Array.AsReadOnly((Degradations ?? []).ToArray());
}
