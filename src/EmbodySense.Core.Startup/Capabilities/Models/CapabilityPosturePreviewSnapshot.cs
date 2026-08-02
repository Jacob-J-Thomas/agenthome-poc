namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Contains a bounded read-only lifecycle impact projection with no mutation authority.</summary>
/// <param name="CapabilityId">The exact capability identity.</param>
/// <param name="Operation">The inspected lifecycle transition.</param>
/// <param name="CurrentVersion">The exact current version.</param>
/// <param name="TargetVersion">The exact proposed version when relevant.</param>
/// <param name="DependentSetHash">The canonical current dependent-set identity.</param>
/// <param name="IsBlocked">Whether a required dependent conflicts.</param>
/// <param name="HasDegradation">Whether an optional dependent would degrade.</param>
/// <param name="Impacts">The deterministic bounded impacts.</param>
/// <param name="ImpactsTruncated">Whether additional safe impacts exceeded the response bound.</param>
public sealed record CapabilityPosturePreviewSnapshot(
    string CapabilityId,
    string Operation,
    string CurrentVersion,
    string? TargetVersion,
    string DependentSetHash,
    bool IsBlocked,
    bool HasDegradation,
    IReadOnlyList<CapabilityPosturePreviewImpactSnapshot> Impacts,
    bool ImpactsTruncated)
{
    /// <summary>Gets a defensive read-only impact snapshot.</summary>
    public IReadOnlyList<CapabilityPosturePreviewImpactSnapshot> Impacts { get; } = Array.AsReadOnly((Impacts ?? []).ToArray());
}
