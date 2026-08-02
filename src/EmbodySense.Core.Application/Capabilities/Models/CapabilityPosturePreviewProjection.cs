namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Contains a bounded deterministic read-only lifecycle impact projection.</summary>
/// <param name="CapabilityId">The exact capability identity.</param>
/// <param name="Operation">The inspected transition.</param>
/// <param name="CurrentVersion">The exact current version.</param>
/// <param name="TargetVersion">The exact proposed version when relevant.</param>
/// <param name="DependentSetHash">The canonical current dependent-set identity.</param>
/// <param name="IsBlocked">Whether a required dependent conflicts with the transition.</param>
/// <param name="HasDegradation">Whether an optional dependent would become degraded.</param>
/// <param name="Impacts">The deterministic bounded impacts.</param>
/// <param name="ImpactsTruncated">Whether additional impacts exceeded the response bound.</param>
/// <remarks>This projection carries no operation identity, revision authorization, or mutation token.</remarks>
public sealed record CapabilityPosturePreviewProjection(
    string CapabilityId,
    CapabilityLifecycleOperationKind Operation,
    string CurrentVersion,
    string? TargetVersion,
    string DependentSetHash,
    bool IsBlocked,
    bool HasDegradation,
    IReadOnlyList<CapabilityPosturePreviewImpact> Impacts,
    bool ImpactsTruncated)
{
    /// <summary>Gets a defensive read-only impact snapshot.</summary>
    public IReadOnlyList<CapabilityPosturePreviewImpact> Impacts { get; } = Array.AsReadOnly((Impacts ?? []).ToArray());
}
